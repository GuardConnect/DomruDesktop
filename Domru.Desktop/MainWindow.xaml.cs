using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Domru.Desktop.Core;
using NAudio.Wave;

namespace Domru.Desktop;
public partial class MainWindow : Window
{
    private SessionStore? store;
    private DomruApi? api;
    private VideoPlayer? video;
    private StompClient? stomp;
    private FcmReceiver? push;
    private readonly List<SipClient> sipClients = [];
    private readonly List<Door> allDoors = [];
    private readonly SemaphoreSlim listenerGate = new(1, 1);
    private readonly SemaphoreSlim doorGate = new(1, 1);
    private CallWindow? callWindow;
    private bool loading, closing, closed, archiveMode,mainLoaded;
    private long selectionVersion, videoVersion;
    private int eventsPage;
    private readonly List<HistoryEvent> history = [];
    private long? archiveTimestamp;
    private HistoryEvent? selectedRecording;
    private bool cameraEventMode;
    private string? historyCamera;
    private DateTimeOffset cameraHistoryFrom, cameraHistoryTo;
    private DispatcherTimer? videoRenewal;
    private Door? Door => DoorBox.SelectedItem as Door;
    private Place? Place => PlaceBox.SelectedItem as Place;

    private record AccountItem(string Label, JsonObject Raw);
    private record DeviceItem(string Label, int Id);
    private record EntranceItem(string Label, string? Id);
    private record EventItem(string DisplayTime, string Title, HistoryEvent Event);
    private record ArchiveRow(string TimeText,string Title,string Day,HistoryEvent Event,string DateText);
    private CancellationTokenSource? archiveCts;
    private string? loadedArchiveDoor;
    private int archiveDaysAgo;
    private DateTime? loadedArchiveDate;
    private double videoAspect=16.0/9;
    private bool fullscreen;
    private Rect savedWindowBounds;
    private bool expandedVideo;
    private WindowState savedWindowState;
    private bool savedTopmost;
    private double savedMinWidth,savedMinHeight;
    private DispatcherTimer? videoLayoutTimer;
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if(mainLoaded)return;mainLoaded=true;
        try
        {
            InitializeBackgroundFeatures();
            if(Environment.GetCommandLineArgs().Contains("--tray"))await HideToTrayAsync();
            ArchiveDate.SelectedDate = DateTime.Today;
            MicrophoneBox.Items.Add(new DeviceItem("По умолчанию", -1));
            SpeakerBox.Items.Add(new DeviceItem("По умолчанию", -1));
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                MicrophoneBox.Items.Add(new DeviceItem(WaveIn.GetCapabilities(i).ProductName, i));
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                SpeakerBox.Items.Add(new DeviceItem(WaveOut.GetCapabilities(i).ProductName, i));
            MicrophoneBox.SelectedIndex = SpeakerBox.SelectedIndex = 0;
            video = new();
            VideoView.DoubleClick += ToggleFullscreen; VideoView.Escape += ExitFullscreen;
            videoLayoutTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
            videoLayoutTimer.Tick+=(_,_)=>{if(!closing&&!closed&&video?.Player.IsPlaying==true){UpdateVideoAspect();VideoView.HookVideoWindows();}};
            videoLayoutTimer.Start();
            video.State += s => Dispatcher.BeginInvoke(() =>
            {
                VideoState.Text = s;
                VideoPlaceholder.Visibility = s == "Воспроизведение" ? Visibility.Collapsed : Visibility.Visible;
                VideoPlaceholder.Text = s;
                VideoEmptyState.Visibility = s == "Воспроизведение" ? Visibility.Collapsed : Visibility.Visible;
                if(s == "Воспроизведение") { VideoView.Visibility=Visibility.Visible; VideoView.HookVideoWindows(); UpdateVideoAspect(); }
            });
            if (Environment.GetCommandLineArgs().Contains("--smoke"))
            {
                await SmokeAsync();
                return;
            }
            if(Environment.GetCommandLineArgs().Contains("--verify-window-chrome")){await VerifyWindowChromeAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-audio-checks")){await VerifyAudioChecksAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-archive-events")){await VerifyArchiveEventsAsync();return;}

            var custom = Environment.GetEnvironmentVariable("DOMRU_DATA_DIR");
            store = new SessionStore(custom);
            RestoreAudioDevices();
            CameraAutoplayBox.IsChecked=store.LoadCameraAutoplay();
            CloseToTrayBox.IsChecked=store.LoadCloseToTray();
            api = new(store.InstallationId, store.Load());
            api.SessionChanged += store.Save;
            if (api.Session is not null)
                await LoadHomeAsync();
            if(Environment.GetCommandLineArgs().Contains("--verify-tray")){await VerifyTrayAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-autoplay")){await VerifyAutoplayAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-services")){await VerifyServicesAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-cycle")){await VerifyVideoCycleAsync();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-archive")){await VerifyArchiveAsync();return;}
            if (Environment.GetCommandLineArgs().Any(x => x is "--verify-live" or "--verify-video" or "--verify-archive" or "--verify-cycle" or "--verify-services" or "--verify-autoplay" or "--verify-tray"))
            {
                await VerifyLiveAsync();
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            if (Environment.GetCommandLineArgs().Any(x => x is "--verify-live" or "--verify-video" or "--verify-archive" or "--verify-cycle" or "--verify-services" or "--verify-autoplay" or "--verify-tray"))
            {
                var root = Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR") ?? Path.Combine(Environment.CurrentDirectory, "artifacts");
                Directory.CreateDirectory(root);
                await File.WriteAllTextAsync(Path.Combine(root, "live-ui-error.txt"), ex.Message);
                RequestExit();
            }
        }
    }

    private async Task BusyAsync(Func<Task> work, string status)
    {
        StatusLabel.Text = status;
        try
        {
            await work();
        }
        catch (Exception e)
        {
            StatusLabel.Text = e.Message;
        }
        finally
        {
            if (StatusLabel.Text == status)
                StatusLabel.Text = "Готово";
        }
    }

    private async void PasswordLoginClick(object sender, RoutedEventArgs e)
    {
        if (api is null)
            return;
        var password = PasswordBox.Password;
        PasswordBox.Clear();
        await BusyAsync(async () =>
        {
            await api.PasswordLoginAsync(LoginBox.Text.Trim(), password);
            await LoadHomeAsync();
        }, "Выполняется вход…");
    }

    private string Phone => new(PhoneBox.Text.Where(char.IsDigit).ToArray());

    private async void FindAccountsClick(object sender, RoutedEventArgs e)
    {
        if (api is null)
            return;
        await BusyAsync(async () =>
        {
            if (Phone.Length < 10)
                throw new InvalidOperationException("Введите номер телефона");
            var result = await api.GetPhoneAccountsAsync(Phone);
            AccountBox.Items.Clear();
            foreach (var a in Json.Array(result).OfType<JsonObject>())
                AccountBox.Items.Add(new AccountItem(Json.Str(a, "address", "accountId", "login") ?? "Договор", a));
            if (AccountBox.Items.Count > 0)
                AccountBox.SelectedIndex = 0;
            else
                StatusLabel.Text = "Если SMS уже отправлено, введите код. Если код не пришёл, используйте вход по договору.";
        }, "Поиск договоров…");
    }

    private async void SendSmsClick(object sender, RoutedEventArgs e)
    {
        if (api is null)
            return;
        await BusyAsync(async () =>
        {
            if (AccountBox.SelectedItem is not AccountItem account)
                throw new InvalidOperationException("Выберите договор");
            await api.SendSmsAsync(Phone, account.Raw);
            StatusLabel.Text = "SMS отправлено";
        }, "Отправка SMS…");
    }

    private async void ConfirmSmsClick(object sender, RoutedEventArgs e)
    {
        if (api is null)
            return;
        await BusyAsync(async () =>
        {
            await api.ConfirmSmsAsync(Phone, CodeBox.Text.Trim(), (AccountBox.SelectedItem as AccountItem)?.Raw ?? new());
            CodeBox.Clear();
            await LoadHomeAsync();
        }, "Подтверждение SMS…");
    }

    private async Task LoadHomeAsync()
    {
        if (api is null)
            return;
        loading = true;
        try
        {
            var places = await api.PlacesAsync();
            allDoors.Clear();
            foreach (var place in places)
                allDoors.AddRange(await api.DoorsAsync(place.Id));
            PlaceBox.ItemsSource = places;
            PlaceBox.SelectedIndex = places.Count > 0 ? 0 : -1;
            LoginPanel.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
            LogoutButton.Visibility = Visibility.Visible;
            StatusLabel.Text = places.Count == 0 ? "К аккаунту не привязаны адреса" : "Выберите камеру или откройте историю событий";
        }
        finally
        {
            loading = false;
        }

        await UpdateDoorsAsync();
        archiveMode=false;EventsPanel.Visibility=Visibility.Collapsed;VideoPanel.Visibility=Visibility.Visible;
        ArchiveToolbar.Visibility=BackButton.Visibility=ForwardButton.Visibility=Visibility.Collapsed;
        SelectNavigation("camera");
        await AutoplayCameraAsync();
        if (Environment.GetCommandLineArgs().Any(x=>x is "--verify-video" or "--verify-archive" or "--verify-cycle" or "--verify-services" or "--verify-autoplay" or "--verify-tray")) return;
        try
        {
            var boot = await api.BootstrapAsync();
            var host = Json.Str(boot?["MOBILE_URL"]?["domain"], "stomp") ?? api.BaseUri.Host;
            if (!Uri.CheckHostName(host).Equals(UriHostNameType.Dns))
                throw new InvalidOperationException("Нет домена WSS");
            if (stomp is not null)
                await stomp.DisposeAsync();
            stomp = new(api, host);
            stomp.Status += s => Dispatcher.BeginInvoke(() => ConnectionLabel.Text = s);
            stomp.Message += HandleStomp;
            stomp.Start();
        }
        catch (Exception e)
        {
            ConnectionLabel.Text = "WSS: " + e.Message;
        }

        loading = true;
        ListenBox.IsChecked = true;
        loading = false;
        await ConfigureListenersAsync();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await BusyAsync(LoadHomeAsync, "Обновление устройств…");
    private async void PlaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
                await BusyAsync(async()=>{await UpdateDoorsAsync();await AutoplayCameraAsync();}, "Обновление адреса…");
    }

    private async Task UpdateDoorsAsync()
    {
        if (Place is null)
            return;
        loading = true;
        try
        {
            DoorBox.ItemsSource = allDoors.Where(d => d.PlaceId == Place.Id).ToList();
            DoorBox.SelectedIndex = DoorBox.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            loading = false;
        }

        await UpdateSelectionAsync();
    }

    private async void DoorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
            await BusyAsync(async()=>{await UpdateSelectionAsync();await AutoplayCameraAsync();}, "Обновление домофона…");
    }

    private async Task UpdateSelectionAsync()
    {
        selectionVersion++;
        videoVersion++;
        if(video is not null)await video.StopAsync();
        videoRenewal?.Stop();
        VideoPlaceholder.Visibility = Visibility.Visible;
        VideoPlaceholder.Text = "Выберите домофон и запустите камеру";
        VideoEmptyState.Visibility = Visibility.Visible;
        VideoView.Visibility = Visibility.Hidden;
        SelectedDoorTitle.Text = Door?.Name ?? "Выберите дверь";
        EntranceBox.Items.Clear();
        EntranceBox.Items.Add(new EntranceItem("Основная дверь", null));
        if (Door is { } door)
        {
            OpenButton.IsEnabled = door.AllowOpen;
            foreach (var en in (door.Raw["entrances"] as JsonArray ?? new()).OfType<JsonObject>())
                EntranceBox.Items.Add(new EntranceItem(Json.Str(en, "name") ?? "Точка доступа", Json.Str(en, "id")));
        }
        else
            OpenButton.IsEnabled = false;
        EntranceBox.SelectedIndex = 0;
        EntranceLabel.Visibility = EntranceBox.Visibility = EntranceBox.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if(archiveMode&&ArchivePanel.Visibility==Visibility.Visible)await LoadArchiveAsync();
        if(KeysPanel.Visibility==Visibility.Visible)await LoadKeysAsync();
        if(AccountPanel.Visibility==Visibility.Visible)await LoadAccountAsync();
    }

    private async void CameraTabClick(object sender,RoutedEventArgs e)=>await OpenCameraTabAsync();
    private async Task OpenCameraTabAsync(bool autoplay=true)
    {
        var previousTarget=navigationTarget;var navigation=BeginNavigation("camera");
        ExitFullscreen();
        if(archiveMode||previousTarget!="camera"){videoVersion++;videoRenewal?.Stop();if(video is not null)await video.StopAsync();}
        if(navigation!=navigationVersion)return;
        SelectNavigation("camera");
        archiveMode = false;
        EventsPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Visible;
        ArchiveToolbar.Visibility = BackButton.Visibility = ForwardButton.Visibility = Visibility.Collapsed;
        ApplyVideoLayout();
        if(autoplay)await AutoplayCameraAsync();
    }

    private async void EventsTabClick(object sender,RoutedEventArgs e)=>await OpenEventsTabAsync();
    private async Task OpenEventsTabAsync()
    {
        cameraEventMode=false;historyCamera=null;
        var navigation=BeginNavigation("events");
        SelectNavigation("events");
        if(video is not null)await video.StopAsync();
        if(navigation!=navigationVersion)return;
        videoRenewal?.Stop();
        VideoPanel.Visibility = Visibility.Collapsed;
        EventsPanel.Visibility = Visibility.Visible;
        await BusyAsync(LoadEventsAsync, "Загрузка событий…");
    }

    private async void ArchiveTabClick(object sender,RoutedEventArgs e)=>await OpenArchiveTabAsync();
    private async Task OpenArchiveTabAsync()
    {
        var navigation=BeginNavigation("archive");
        ExitFullscreen();videoVersion++;videoRenewal?.Stop();if(video is not null)await video.StopAsync();
        if(navigation!=navigationVersion)return;
        SelectNavigation("archive");
        archiveMode = true;
        EventsPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Collapsed;
        ArchivePanel.Visibility = Visibility.Visible;
        ArchiveToolbar.Visibility = BackButton.Visibility = ForwardButton.Visibility = Visibility.Visible;
        await BusyAsync(LoadArchiveAsync,"Загрузка архива…");
    }
    private void SelectNavigation(string page)
    {
        if(page!="camera")videoVersion++;
        NavKeys.Tag=page=="keys"?"Active":null;NavPayments.Tag=page=="payments"?"Active":null;
        KeysPanel.Visibility=page=="keys"?Visibility.Visible:Visibility.Collapsed;AccountPanel.Visibility=page=="payments"?Visibility.Visible:Visibility.Collapsed;
        NavCamera.Tag = page == "camera" ? "Active" : null;
        NavEvents.Tag = page == "events" ? "Active" : null;
        NavArchive.Tag = page == "archive" ? "Active" : null;
        NavSettings.Tag = page == "settings" ? "Active" : null;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if(page!="archive") { ArchivePanel.Visibility=Visibility.Collapsed; archiveCts?.Cancel(); }
        PageTitle.Text = page switch { "events" => "События", "archive" => "Архив", "settings" => "Настройки", "keys"=>"Ключи", "payments"=>"Оплата домофона", _ => "Камера" };
        PageDescription.Text = page switch { "events" => "История звонков и открытий двери", "archive" => "События домофона по дням", "settings" => "Автозагрузка, работа в фоне и звук звонков", "keys"=>"Ключи, привязанные к выбранному адресу", "payments"=>"Дом.ру и Цифрал-Сервис", _ => "Посмотрите, что происходит у вашего входа" };
        ApplyVideoLayout();
    }
    private async void SettingsClick(object sender,RoutedEventArgs e)=>await OpenSettingsTabAsync();
    private async Task OpenSettingsTabAsync()
    {
        var navigation=BeginNavigation("settings");
        ExitFullscreen();videoVersion++;videoRenewal?.Stop();if(video is not null)await video.StopAsync();
        if(navigation!=navigationVersion)return;
        SelectNavigation("settings");
        VideoPanel.Visibility = EventsPanel.Visibility = Visibility.Collapsed;
    }
    private async void MinimizeClick(object sender,RoutedEventArgs e)=>await HideToTrayAsync();
    private void MaximizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private void CloseWindowClick(object sender,RoutedEventArgs e)=>Close();
    private void RoundPlaceholder(object sender,SizeChangedEventArgs e)=>((UIElement)sender).Clip=new RectangleGeometry(new Rect(e.NewSize),18,18);
    private async Task<IntPtr> EnsureVideoHostAsync()
    {
        VideoPanel.Visibility=Visibility.Visible;VideoView.Visibility=Visibility.Visible;VideoEmptyState.Visibility=Visibility.Collapsed;
        UpdateLayout();ResizeVideoFrame();
        for(int i=0;i<20;i++)
        {
            await Dispatcher.InvokeAsync(()=>UpdateLayout(),DispatcherPriority.Loaded);
            if(VideoView.Handle!=IntPtr.Zero)return VideoView.Handle;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Не удалось подготовить встроенный экран камеры");
    }
    private void ExpandVideoClick(object sender,RoutedEventArgs e)
    {
        expandedVideo=!expandedVideo;ApplyVideoLayout();
    }
    private void ApplyVideoLayout()
    {
        if(fullscreen)return;
        var expanded=expandedVideo&&VideoPanel.Visibility==Visibility.Visible&&(NavCamera.Tag is "Active"||NavArchive.Tag is "Active");
        VideoPanelCard.CornerRadius=VideoImageCard.CornerRadius=VideoFrame.CornerRadius=new CornerRadius(18);
        VideoView.CornerRadius=18;VideoFrame.BorderThickness=new Thickness(0);
        VideoPanelCard.Background=archiveMode?Brushes.White:Brushes.Transparent;
        VideoPanelCard.BorderThickness=new Thickness(archiveMode?1:0);
        VideoPanelCard.Padding=new Thickness(archiveMode?12:0);
        VideoImageCard.BorderThickness=new Thickness(archiveMode?0:1);
        VideoImageCard.Padding=new Thickness(archiveMode?0:10);
        VideoToolbar.BorderThickness=new Thickness(archiveMode?0:1);
        VideoToolbar.Padding=archiveMode?new Thickness(0,10,0,0):new Thickness(16,12,16,12);
        ArchiveToolbar.BorderThickness=new Thickness(0);
        ArchiveToolbar.Padding=new Thickness(0,0,0,10);
        ArchiveToolbar.Margin=new Thickness(0,0,0,0);
        CameraControlsTitle.Visibility=VideoModesGroup.Visibility=archiveMode?Visibility.Collapsed:Visibility.Visible;
        if(archiveMode&&WatchStreamButton.Parent==PlaybackButtonsGroup)
        {
            PlaybackButtonsGroup.Children.Remove(WatchStreamButton);PlaybackButtonsGroup.Children.Remove(StopStreamButton);
            VideoActionsGroup.Children.Insert(0,StopStreamButton);VideoActionsGroup.Children.Insert(0,WatchStreamButton);
        }
        else if(!archiveMode&&WatchStreamButton.Parent==VideoActionsGroup)
        {
            VideoActionsGroup.Children.Remove(WatchStreamButton);VideoActionsGroup.Children.Remove(StopStreamButton);
            PlaybackButtonsGroup.Children.Add(WatchStreamButton);PlaybackButtonsGroup.Children.Add(StopStreamButton);
        }
        ToolbarActionsGrid.Visibility=ToolbarSeparator.Visibility=archiveMode?Visibility.Collapsed:Visibility.Visible;
        VideoActionsRow.Margin=archiveMode?new Thickness(0):new Thickness(0,12,0,0);
        SnapshotButton.Margin=archiveMode?new Thickness(7,0,0,0):new Thickness(0);
        WatchStreamButton.Content=archiveMode?"Просмотреть запись":"Смотреть стрим камеры";
        StopStreamButton.Content=archiveMode?"Закрыть запись":"Остановить стрим камеры";
        VideoEmptyDescription.Text=archiveMode?"Нажмите «Просмотреть запись», чтобы воспроизвести выбранное событие":"Выберите домофон и нажмите «Смотреть стрим камеры»";
        if(VideoEmptyState.Visibility==Visibility.Visible)VideoPlaceholder.Text=archiveMode?"Запись готова к просмотру":"Камера готова к просмотру";
        ExpandVideoText.Text=expanded?"Обычный размер":"Увеличить";
        ExpandVideoGlyph.Text=expanded?"\uE73F":"\uE740";
        DoorSidebar.Visibility=expanded?Visibility.Collapsed:Visibility.Visible;
        SideColumn.Width=new GridLength(expanded?0:265);SideGap.Width=new GridLength(expanded?0:22);
        BrandRow.Height=new GridLength(expanded?0:54);
        PageHeader.Visibility=expanded?Visibility.Collapsed:Visibility.Visible;
        UpdateLayout();ResizeVideoFrame();
    }
    private void VideoStageChanged(object sender,SizeChangedEventArgs e)=>ResizeVideoFrame();
    private void UpdateVideoAspect()
    {
        uint width=0,height=0;
        if(video is not null&&video.Player.Size(0,ref width,ref height)&&width>0&&height>0)videoAspect=(double)width/height;
        ResizeVideoFrame();
    }
    private void ResizeVideoFrame()
    {
        if(VideoStage is null||VideoFrame is null)return;
        var width=VideoStage.ActualWidth;var height=VideoStage.ActualHeight;
        if(width<=0||height<=0)return;
        VideoFrame.Width=double.NaN;VideoFrame.Height=double.NaN;
        VideoFrame.HorizontalAlignment=HorizontalAlignment.Stretch;VideoFrame.VerticalAlignment=VerticalAlignment.Stretch;
    }
    private void FullscreenClick(object sender,RoutedEventArgs e)=>ToggleFullscreen();
    private void WindowKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape&&fullscreen){ExitFullscreen();e.Handled=true;}}
    private void ToggleFullscreen()
    {
        if(fullscreen){ExitFullscreen();return;}if(VideoPanel.Visibility!=Visibility.Visible)return;
        
        savedWindowBounds=new Rect(Left,Top,ActualWidth,ActualHeight);savedWindowState=WindowState;savedTopmost=Topmost;savedMinWidth=MinWidth;savedMinHeight=MinHeight;MinWidth=MinHeight=0;fullscreen=true;
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight=0;
        CaptionRow.Height=BrandRow.Height=FooterRow.Height=new GridLength(0);NavigationBar.Visibility=PageHeader.Visibility=DoorSidebar.Visibility=Visibility.Collapsed;
        SideColumn.Width=SideGap.Width=new GridLength(0);HomePanel.Margin=new Thickness(0);VideoToolbar.Visibility=ArchiveToolbar.Visibility=Visibility.Collapsed;
        ToolbarRow.Height=new GridLength(0);
        VideoStage.Background=Brushes.Black;VideoFrame.BorderThickness=new Thickness(0);
        VideoPanelCard.Padding=VideoImageCard.Padding=new Thickness(0);
        VideoPanelCard.BorderThickness=VideoImageCard.BorderThickness=new Thickness(0);
        VideoPanelCard.CornerRadius=VideoImageCard.CornerRadius=VideoFrame.CornerRadius=new CornerRadius(0);
        VideoPanelCard.Background=VideoImageCard.Background=VideoFrame.Background=Brushes.Black;
        VideoView.CornerRadius=0;
        WindowState=WindowState.Normal;var monitor=VideoWindowBounds.Get(this);Left=monitor.X;Top=monitor.Y;Width=monitor.Width;Height=monitor.Height;Topmost=true;Activate();Focus();Keyboard.Focus(this);UpdateLayout();ResizeVideoFrame();
    }
    private void ExitFullscreen()
    {
        if(!fullscreen)return;fullscreen=false;System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight=84;Topmost=savedTopmost;MinWidth=savedMinWidth;MinHeight=savedMinHeight;
        CaptionRow.Height=new GridLength(30);BrandRow.Height=new GridLength(54);FooterRow.Height=new GridLength(34);
        NavigationBar.Visibility=PageHeader.Visibility=DoorSidebar.Visibility=Visibility.Visible;SideColumn.Width=new GridLength(265);SideGap.Width=new GridLength(22);
        HomePanel.Margin=new Thickness(28,22,28,20);VideoToolbar.Visibility=Visibility.Visible;ArchiveToolbar.Visibility=archiveMode?Visibility.Visible:Visibility.Collapsed;
        ToolbarRow.Height=GridLength.Auto;ApplyVideoLayout();
        VideoStage.Background=Brushes.Transparent;VideoImageCard.Background=VideoFrame.Background=Brushes.White;VideoFrame.BorderThickness=new Thickness(0);
        WindowState=WindowState.Normal;Left=savedWindowBounds.X;Top=savedWindowBounds.Y;Width=savedWindowBounds.Width;Height=savedWindowBounds.Height;WindowState=savedWindowState;UpdateLayout();ResizeVideoFrame();
    }
    private async Task LoadArchiveAsync()
    {
        if(api is null||Door is not {} door)return;
        archiveCts?.Cancel();archiveCts?.Dispose();archiveCts=new();var current=archiveCts;
        ArchiveProgress.Visibility=Visibility.Visible;ArchiveRefresh.IsEnabled=false;ArchiveStatus.Text="Загружаем все страницы истории…";ArchiveEmptyText.Visibility=Visibility.Collapsed;
        try
        {
            var now=DateTimeOffset.Now;var day=archiveDaysAgo;SetArchiveDay(day);var rows=await ArchiveHistory.LoadDayAsync(api,door,now,day,current.Token,new Progress<int>(count=>{if(archiveCts==current)ArchiveStatus.Text=$"Загружено событий: {count}…";}));
            if(current.IsCancellationRequested||Door?.Id!=door.Id)return;
            RenderArchive(rows);loadedArchiveDoor=door.Id;loadedArchiveDate=now.Date.AddDays(-day);ArchiveStatus.Text=$"Событий: {rows.Count}  ·  {door.Name}";
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(archiveCts==current)ArchiveStatus.Text="Не удалось загрузить архив: "+e.Message;}
        finally{if(archiveCts==current){ArchiveProgress.Visibility=Visibility.Collapsed;ArchiveRefresh.IsEnabled=true;}}
    }
    private void RenderArchive(List<HistoryEvent> events)
    {
        var culture=System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var rows=events.Where(x=>x.Time is not null).Select(x=>new ArchiveRow(EventCaption.ArchiveTime(x),EventCaption.ArchiveTitle(x),x.Time!.Value.LocalDateTime.ToString("dddd, dd.MM.yyyy",culture),x,EventCaption.ArchiveDates(x))).ToList();
        ArchiveList.ItemsSource=rows;ArchiveEmptyText.Visibility=rows.Count==0?Visibility.Visible:Visibility.Collapsed;
    }
    private async void ArchiveDayClick(object sender,RoutedEventArgs e)
    {
        if(sender is not Button button||!int.TryParse(button.CommandParameter?.ToString(),out var day)||day==archiveDaysAgo)return;
        SetArchiveDay(day);ArchiveList.ItemsSource=null;
        await LoadArchiveAsync();
    }
    private void SetArchiveDay(int day)
    {
        archiveDaysAgo=day;ArchiveToday.Tag=day==0?"Active":null;ArchiveYesterday.Tag=day==1?"Active":null;ArchiveDayBefore.Tag=day==2?"Active":null;
        ArchiveSelectedDate.Text=DateTime.Now.Date.AddDays(-day).ToString("dd.MM.yyyy");ArchiveWeekday.Text=DateTime.Now.Date.AddDays(-day).ToString("dddd",System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    }
    private async void ArchiveRefreshClick(object sender,RoutedEventArgs e)=>await LoadArchiveAsync();
    private bool archiveReturnToEvents;
    private void ShowArchivePlayer(bool fromEvents=false)
    {
        archiveReturnToEvents=fromEvents;
        SelectNavigation("archive");archiveMode=true;ArchivePanel.Visibility=EventsPanel.Visibility=Visibility.Collapsed;VideoPanel.Visibility=Visibility.Visible;ArchiveToolbar.Visibility=BackButton.Visibility=ForwardButton.Visibility=Visibility.Visible;
        ApplyVideoLayout();
        PageDescription.Text="Просмотр архивной записи камеры";
        UpdateRecordingInterval();
    }
    private void ArchiveManualClick(object sender,RoutedEventArgs e)=>ShowArchivePlayer();
    private async void ArchiveBackClick(object sender,RoutedEventArgs e){VideoPanel.Visibility=Visibility.Collapsed;ArchivePanel.Visibility=Visibility.Visible;if(video is not null)await video.StopAsync();if(loadedArchiveDoor!=Door?.Id||loadedArchiveDate!=DateTime.Now.Date.AddDays(-archiveDaysAgo))await LoadArchiveAsync();}
    private async void ArchiveItemClick(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {Tag:ArchiveRow row}||row.Event.Time is null)return;
        selectedRecording=row.Event;ArchiveDate.SelectedDate=row.Event.Time.Value.LocalDateTime.Date;ArchiveTime.Text=row.Event.Time.Value.ToLocalTime().ToString("HH:mm:ss");ShowArchivePlayer();await BusyAsync(()=>PlayAsync(row.Event.Time.Value.ToUnixTimeSeconds()),"Загрузка записи события…");
    }

    private int eventsLoadingVersion;
    private async Task LoadEventsAsync()
    {
        if(api is null||Place is null)return;
        var request=++eventsLoadingVersion;EventsProgress.Visibility=Visibility.Visible;EventsRefresh.IsEnabled=false;
        try{await LoadEventsCoreAsync();}
        finally{if(request==eventsLoadingVersion){EventsProgress.Visibility=Visibility.Collapsed;EventsRefresh.IsEnabled=true;}}
    }
    private async Task LoadEventsCoreAsync()
    {
        if (api is null || Place is null)
            return;
        var version = selectionVersion;
        eventsPage = 0;
        if(cameraEventMode&&historyCamera is { } selectedCamera)
        {
            var from=cameraHistoryFrom;var to=from.AddDays(1);
            var rows=DomruApi.ParseEvents(await api.CameraEventsAsync(selectedCamera,from,to));
            if(version!=selectionVersion||!cameraEventMode||historyCamera!=selectedCamera||cameraHistoryFrom!=from)return;
            cameraHistoryTo=to;history.Clear();history.AddRange(rows);MoreEventsButton.Visibility=Visibility.Visible;MoreEventsButton.IsEnabled=rows.Count==200&&rows.Any(x=>x.Time is not null);RenderHistory();return;
        }
        history.Clear();
        try
        {
            var page = await api.SearchEventsAsync(Place.Id);
            if (version != selectionVersion)
                return;
            history.AddRange(page.Events);
            MoreEventsButton.IsEnabled = !page.Last;
            MoreEventsButton.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            history.AddRange(await api.EventsAsync(Place.Id));
            MoreEventsButton.Visibility = Visibility.Collapsed;
        }

        RenderHistory();
    }

    private void RenderHistory()
    {
        EventsGrid.ItemsSource = history.OrderByDescending(x => x.Time).Select(x => new EventItem(x.Time?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "—", cameraEventMode?EventCaption.CameraDayDescription(x):EventCaption.Describe(x), x)).ToList();
        EventsTitle.Text = cameraEventMode?$"События дня · {cameraHistoryFrom:dd.MM.yyyy}":"История событий";
        EventsCount.Text = $"Событий: {history.Count}";
    }

    private async void MoreEventsClick(object sender, RoutedEventArgs e)
    {
        if (api is null || Place is null)
            return;
        await BusyAsync(async () =>
        {
            MoreEventsButton.IsEnabled = false;
            if (cameraEventMode && historyCamera is not null)
            {
                var oldest = history.Where(x => x.Time is not null).Select(x => x.Time!.Value).Min();
                cameraHistoryTo = oldest.AddMilliseconds(-1);
                if (cameraHistoryTo <= cameraHistoryFrom)
                    return;
                var rows = DomruApi.ParseEvents(await api.CameraEventsAsync(historyCamera, cameraHistoryFrom, cameraHistoryTo));
                var added = rows.Where(x => !history.Any(old => old.Id == x.Id)).ToList();
                history.AddRange(added);
                RenderHistory();
                MoreEventsButton.IsEnabled = rows.Count == 200 && added.Count > 0;
                return;
            }

            var page = await api.SearchEventsAsync(Place.Id, eventsPage + 1);
            eventsPage++;
            history.AddRange(page.Events.Where(x => !history.Any(old => old.Id == x.Id)));
            RenderHistory();
            MoreEventsButton.IsEnabled = !page.Last;
        }, "Загрузка истории…");
    }

    private async void EventsRefreshClick(object sender, RoutedEventArgs e) => await BusyAsync(LoadEventsAsync, "Обновление событий…");
    private async void EventDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EventsGrid.SelectedItem is not EventItem item || item.Event.Time is null)
            return;
        var source = Json.Str(item.Event.Raw["source"], "id");
        var target = allDoors.FirstOrDefault(d => d.PlaceId == Place?.Id && (item.Event.CameraId is not null && d.CameraId == item.Event.CameraId || source is not null && d.Id == source));
        if (target is not null && target != Door)
        {
            loading = true;
            DoorBox.SelectedItem = target;
            loading = false;
            await UpdateSelectionAsync();
        }

        ArchiveDate.SelectedDate = item.Event.Time.Value.LocalDateTime.Date;
        selectedRecording=item.Event;
        ArchiveTime.Text = item.Event.Time.Value.LocalDateTime.ToString("HH:mm:ss");
        ShowArchivePlayer(fromEvents:true);
        await BusyAsync(() => PlayAsync(item.Event.Time.Value.ToUnixTimeSeconds()), "Загрузка записи события…");
    }

    private async void PlayClick(object sender, RoutedEventArgs e)
    {
        if (archiveMode)
        {
            PlayArchiveClick(sender, e);
            return;
        }

        await BusyAsync(() => PlayAsync(), "Подключение камеры…");
    }

    private async Task PlayAsync(long? timestamp = null)
    {
        if (api is null || video is null || Door is not { } door)
            return;
        if (!door.AllowVideo || door.CameraId is null)
            throw new InvalidOperationException("Камера недоступна для этого домофона");
        var version = ++videoVersion;
        var url = await api.VideoAsync(door.CameraId, timestamp, door.TimeZone);
        if (version != videoVersion || closed)
            return;
        archiveTimestamp = timestamp;
        if(timestamp is not null)UpdateRecordingInterval();
        var host=await EnsureVideoHostAsync();
        if(version != videoVersion || closing)return;
        await video.PlayAsync(url,host, VideoMuteBox.IsChecked == true, live: timestamp is null, minimumLatency: LowLatencyBox.IsChecked == true);
        videoRenewal?.Stop();
        if (timestamp is null)
        {
            videoRenewal = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(8)
            };
            videoRenewal.Tick += async (_, _) =>
            {
                if (version == videoVersion)
                    await BusyAsync(() => PlayAsync(), "Обновление камеры…");
            };
            videoRenewal.Start();
        }
    }

    private async void PlayArchiveClick(object sender, RoutedEventArgs e) => await BusyAsync(async () =>
    {
        if (ArchiveDate.SelectedDate is not { } date || !TimeSpan.TryParse(ArchiveTime.Text, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new InvalidOperationException("Введите дату и время ЧЧ:ММ:СС");
        await PlayAsync(new DateTimeOffset(DateTime.SpecifyKind(date + time, DateTimeKind.Local)).ToUnixTimeSeconds());
    }, "Загрузка архива…");
    private void UpdateRecordingInterval()
    {
        var item=selectedRecording;
        if(item is null&&ArchiveDate.SelectedDate is { } date&&TimeSpan.TryParse(ArchiveTime.Text,out var time))item=new("", "",new DateTimeOffset(DateTime.SpecifyKind(date+time,DateTimeKind.Local)),null,new());
        if(item is null)return;
        ArchiveInterval.Text=EventCaption.FullRecordingInterval(item);
        if(EventCaption.RecordingRange(item) is { } range){RecordingStartDate.Text=range.Start.ToString("dd.MM.yyyy");RecordingStartTime.Text=range.Start.ToString("HH:mm:ss");RecordingEndDate.Text=range.End.ToString("dd.MM.yyyy");RecordingEndTime.Text=range.End.ToString("HH:mm:ss");}
        ArchiveIntervalButton.ToolTip=ArchiveInterval.Text+"\nНажмите, чтобы скопировать";
    }
    private int intervalCopyVersion;
    private async void CopyRecordingIntervalClick(object sender,RoutedEventArgs e)
    {
        try{Clipboard.SetText(ArchiveInterval.Text);var version=++intervalCopyVersion;IntervalCopyHint.Text="Скопировано";await Task.Delay(1600);if(version==intervalCopyVersion)IntervalCopyHint.Text="Копировать";}
        catch(System.Runtime.InteropServices.COMException){IntervalCopyHint.Text="Повторите копирование";}
    }
    private async void ArchiveEventsClick(object sender, RoutedEventArgs e)
        =>await OpenCameraDayEventsAsync();
    private async Task OpenCameraDayEventsAsync()
    {
        if (api is null || Door?.CameraId is not { } camera || ArchiveDate.SelectedDate is not { } date)
            return;
        await BusyAsync(async () =>
        {
            var from = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
            var raw = await api.CameraEventsAsync(camera, from, from.AddDays(1));
            var events = DomruApi.ParseEvents(raw);
            history.Clear();
            history.AddRange(events);
            historyCamera = camera;
            cameraHistoryFrom = from;
            cameraHistoryTo = from.AddDays(1);
            cameraEventMode = true;
            SelectNavigation("events");PageDescription.Text=$"События камеры за {date:dd.MM.yyyy}";RenderHistory();
            MoreEventsButton.Visibility = Visibility.Visible;
            MoreEventsButton.IsEnabled = events.Count == 200 && events.Any(x => x.Time is not null);
            if(video is not null)await video.StopAsync();
            VideoPanel.Visibility = Visibility.Collapsed;
            EventsPanel.Visibility = Visibility.Visible;
        }, "Загрузка событий камеры…");
    }

    private async void StopClick(object sender, RoutedEventArgs e)
    {
        if(archiveMode){await CloseRecordingAsync();return;}
        videoVersion++;
        videoRenewal?.Stop();
        if(video is not null)await video.StopAsync();
        VideoPlaceholder.Visibility = Visibility.Visible;
        VideoPlaceholder.Text = "Камера остановлена";
        VideoEmptyState.Visibility = Visibility.Visible;
        VideoView.Visibility = Visibility.Hidden;
        VideoState.Text = "Камера остановлена";
    }
    private async Task CloseRecordingAsync()
    {
        ExitFullscreen();videoVersion++;videoRenewal?.Stop();if(video is not null)await video.StopAsync();
        VideoPanel.Visibility=Visibility.Collapsed;
        if(archiveReturnToEvents)
        {
            BeginNavigation("events");archiveMode=false;SelectNavigation("events");EventsPanel.Visibility=Visibility.Visible;RenderHistory();
            if(cameraEventMode)PageDescription.Text=$"События камеры за {cameraHistoryFrom:dd.MM.yyyy}";
        }
        else{SelectNavigation("archive");ArchivePanel.Visibility=Visibility.Visible;}
        VideoState.Text="Запись закрыта";
    }

    private void VideoMuteChanged(object sender, RoutedEventArgs e)
    {
        if (video is not null)
            video.Player.Mute = VideoMuteBox.IsChecked == true;
    }
    private async void LowLatencyChanged(object sender, RoutedEventArgs e)
    {
        if (api is not null && video?.Player.IsPlaying == true && archiveTimestamp is null)
            await BusyAsync(() => PlayAsync(), "Переключение режима прямого эфира…");
    }

    private async void PauseClick(object sender, RoutedEventArgs e)
    {
        if (video is null) return;
        if (archiveTimestamp is not null) { video.Player.Pause(); return; }
        if (video.Player.IsPlaying) { video.Player.SetPause(true); videoRenewal?.Stop(); }
        else await BusyAsync(() => PlayAsync(), "Возврат к прямому эфиру…");
    }
    private async void BackClick(object sender, RoutedEventArgs e) => await StepArchiveAsync(-30);
    private async void ForwardClick(object sender, RoutedEventArgs e) => await StepArchiveAsync(30);
    private async Task StepArchiveAsync(int seconds)
    {
        if (archiveTimestamp is not { } start || video is null)
            return;
        var ts = start + Math.Max(0, video.Player.Time) / 1000 + seconds;
        await BusyAsync(async () =>
        {
            await PlayAsync(ts);
            var local = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            ArchiveDate.SelectedDate = local.Date;
            ArchiveTime.Text = local.ToString("HH:mm:ss");
        }, "Перемотка архива…");
    }

    private async Task VerifyLiveAsync()
    {
        var root = Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR") ?? Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(root);
        await LoadEventsAsync();
        await PlayAsync();
        await Task.Delay(10000);
        using var media = video!.Player.Media;
        var stats = media?.Statistics;
        var result = new JsonObject
        {
            ["wss"] = ConnectionLabel.Text,
            ["fcm"] = PushLabel.Text,
            ["sip"] = SipLabel.Text,
            ["events"] = history.Count,
            ["videoFrames"] = stats?.DecodedVideo,
            ["audioFrames"] = stats?.DecodedAudio,
            ["playing"] = video.Player.IsPlaying
        };
        await File.WriteAllTextAsync(Path.Combine(root, "live-ui-verification.json"), result.ToJsonString(new() { WriteIndented = true }));
        Close();
    }
    private async Task VerifyArchiveAsync()
    {
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??Path.Combine(Environment.CurrentDirectory,"artifacts");Directory.CreateDirectory(root);
        var days=new JsonArray();SelectNavigation("archive");archiveMode=true;VideoPanel.Visibility=EventsPanel.Visibility=Visibility.Collapsed;ArchivePanel.Visibility=Visibility.Visible;
        for(int day=0;day<3;day++)
        {
            SetArchiveDay(day);await LoadArchiveAsync();var dayRows=ArchiveList.Items.Cast<ArchiveRow>().ToList();var selected=DateTime.Now.Date.AddDays(-day);
            if(dayRows.Any(x=>x.Event.Time!.Value.LocalDateTime.Date!=selected))throw new InvalidOperationException("В архиве события соседнего дня");
            if(ArchiveSelectedDate.Text!=selected.ToString("dd.MM.yyyy"))throw new InvalidOperationException("Дата архива отображается неверно");
            days.Add(new JsonObject{["daysAgo"]=day,["date"]=ArchiveSelectedDate.Text,["events"]=dayRows.Count});
        }
        SetArchiveDay(0);await LoadArchiveAsync();
        var rows=ArchiveList.Items.Cast<ArchiveRow>().ToList();if(rows.Count==0)throw new InvalidOperationException(ArchiveStatus.Text);
        var first=rows[0];ArchiveDate.SelectedDate=first.Event.Time!.Value.LocalDateTime.Date;ArchiveTime.Text=first.Event.Time!.Value.ToLocalTime().ToString("HH:mm:ss");ShowArchivePlayer();await PlayAsync(first.Event.Time.Value.ToUnixTimeSeconds());await Task.Delay(7000);
        using var media=video!.Player.Media;var stats=media?.Statistics;
        var result=new JsonObject{["days"]=days,["events"]=rows.Count,["dateGroups"]=rows.Select(x=>x.Day).Distinct().Count(),["playing"]=video.Player.IsPlaying,["videoFrames"]=stats?.DecodedVideo,["audioFrames"]=stats?.DecodedAudio};
        await File.WriteAllTextAsync(Path.Combine(root,"archive-verification.json"),result.ToJsonString(new(){WriteIndented=true}));Close();
    }
    private async Task VerifyVideoCycleAsync()
    {
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??Path.Combine(Environment.CurrentDirectory,"artifacts");Directory.CreateDirectory(root);
        await Task.Delay(3000);
        if(video?.Player.IsPlaying!=true)throw new InvalidOperationException("Камера не запустилась автоматически");
        var originalWidth=Width;var originalHeight=Height;Width=1080;Height=740;ApplyVideoLayout();await Task.Delay(300);
        var normalSize=new Size(VideoFrame.ActualWidth,VideoFrame.ActualHeight);
        ExpandVideoClick(this,new());await Task.Delay(300);var expandedSize=new Size(VideoFrame.ActualWidth,VideoFrame.ActualHeight);
        if(expandedSize.Width<=normalSize.Width)throw new InvalidOperationException("Увеличение камеры не освобождает место");
        ExpandVideoClick(this,new());Width=originalWidth;Height=originalHeight;ApplyVideoLayout();
        await File.WriteAllTextAsync(Path.Combine(root,"camera-layout.json"),new JsonObject{["automaticStartup"]=true,["smallWindowWidth"]=1080,["smallWindowHeight"]=740,["videoWidth"]=normalSize.Width,["videoHeight"]=normalSize.Height,["expandedWidth"]=expandedSize.Width,["expandedHeight"]=expandedSize.Height}.ToJsonString(new(){WriteIndented=true}));
        var door=Door??throw new InvalidOperationException("Нет домофона для проверки");var events=await ArchiveHistory.LoadAsync(api!,door,DateTimeOffset.Now);var recorded=events.First(x=>x.Time is not null);var cycles=new JsonArray();
        var beats=0;var heartbeat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};heartbeat.Tick+=(_,_)=>beats++;heartbeat.Start();
        for(int cycle=0;cycle<3;cycle++)
        {
            archiveMode=false;SelectNavigation("camera");VideoPanel.Visibility=Visibility.Visible;await PlayAsync();await Task.Delay(6000);
            if(video!.Player.Hwnd!=VideoView.Handle||VideoView.Handle==IntPtr.Zero)throw new InvalidOperationException("Видео не встроено в окно");
            UpdateVideoAspect();var aspectError=Math.Abs(VideoFrame.ActualWidth/VideoFrame.ActualHeight-videoAspect);
            if(cycle==0)
            {
                SendMessage(VideoView.Handle,0x0201,IntPtr.Zero,IntPtr.Zero);await Task.Delay(100);SendMessage(VideoView.Handle,0x0201,IntPtr.Zero,IntPtr.Zero);await Task.Delay(300);if(!fullscreen)throw new InvalidOperationException("Двойной клик не включает полный экран");
                SendMessage(VideoView.Handle,0x0100,new IntPtr(27),IntPtr.Zero);await Task.Delay(300);if(fullscreen)throw new InvalidOperationException("Esc не возвращает встроенный вид");
            }
            var before=beats;await video.StopAsync();archiveMode=true;SelectNavigation("archive");ArchivePanel.Visibility=Visibility.Visible;VideoPanel.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);
            ShowArchivePlayer();await PlayAsync(recorded.Time!.Value.ToUnixTimeSeconds());await Task.Delay(6000);using var media=video.Player.Media;var stats=media?.Statistics;
            cycles.Add(new JsonObject{["cycle"]=cycle+1,["embedded"]=video.Player.Hwnd==VideoView.Handle,["playing"]=video.Player.IsPlaying,["frames"]=stats?.DecodedVideo,["uiHeartbeat"]=beats-before,["aspectError"]=aspectError});
            if(!video.Player.IsPlaying||stats?.DecodedVideo==0)throw new InvalidOperationException("Архив не воспроизводится после прямого эфира");
        }
        heartbeat.Stop();await File.WriteAllTextAsync(Path.Combine(root,"video-cycle.json"),new JsonObject{["doubleClickFullscreen"]=true,["escapeRestore"]=true,["cycles"]=cycles}.ToJsonString(new(){WriteIndented=true}));Close();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);

    private async void SnapshotClick(object sender, RoutedEventArgs e)
    {
        if (api is null || Door is not { } door) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG без потери качества|*.png",
            FileName = $"Домофон-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return;
        await BusyAsync(async () =>
        {
            byte[] bytes;
            if (video is not null && (video.Player.IsPlaying || archiveMode))
                bytes = await video.SnapshotAsync();
            else
                bytes = await api.SnapshotAsync(door);
            using var input = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using (var output = File.Create(dialog.FileName)) encoder.Save(output);
            StatusLabel.Text = $"Снимок сохранён: {frame.PixelWidth} × {frame.PixelHeight}"
                + (frame.PixelWidth < 1920 || frame.PixelHeight < 1080 ? " — исходная камера передаёт разрешение ниже Full HD" : "");
        }, "Сохранение исходного кадра…");
    }

    private async void OpenDoorClick(object sender, RoutedEventArgs e)
    {
        if (api is null || Door is not { } door)
            return;
        if (!await doorGate.WaitAsync(0))
            return;
        OpenButton.IsEnabled = false;
        try
        {
            await BusyAsync(async () =>
            {
                await api.OpenAsync(door, (EntranceBox.SelectedItem as EntranceItem)?.Id);
                StatusLabel.Text = "Команда открытия принята";
            }, "Открытие двери…");
        }
        finally
        {
            OpenButton.IsEnabled = door.AllowOpen;
            doorGate.Release();
        }
    }

    private async void ListenChanged(object sender, RoutedEventArgs e)
    {
        if (loading || api is null)
            return;
        await BusyAsync(ConfigureListenersAsync, "Настройка звонков…");
    }

    private async Task ConfigureListenersAsync()
    {
        await listenerGate.WaitAsync();
        try
        {
            await StopSipAsync();
            if (ListenBox.IsChecked != true || closed)
                return;
            bool onDemand = false;
            try
            {
                push = new FcmReceiver(store!);
                push.Status += s => Dispatcher.BeginInvoke(() => PushLabel.Text = s);
                await push.PrepareAsync();
                await api!.RegisterPushAsync(push.Token);
                onDemand = true;
                push.Message += HandlePush;
            }
            catch (Exception ex)
            {
                PushLabel.Text = "FCM: " + ex.Message + "; резервный SIP";
                if (push is not null)
                    await push.DisposeAsync();
                push = null;
            }

            foreach (var door in allDoors.Where(d => Json.Bool(d.Raw, "allowCallMobile") && d.Type == "SIP"))
            {
                var cred = await api!.SipAsync(door);
                var client = new SipClient(cred, door);
                client.Status += s => Dispatcher.BeginInvoke(() => SipLabel.Text = s);
                client.Incoming += call => Dispatcher.BeginInvoke(() => ShowCall(client, call));
                client.Ended += reason => Dispatcher.BeginInvoke(() =>
                {
                    if (callWindow?.Client == client)
                        callWindow.End(reason);
                });
                sipClients.Add(client);
                await client.StartAsync(onDemand: onDemand, fcmToken: push?.Token);
            }

            push?.Start();
        }
        finally
        {
            listenerGate.Release();
        }
    }

    private async Task StopSipAsync()
    {
        if (push is not null)
        {
            await push.DisposeAsync();
            push = null;
        }

        foreach (var c in sipClients.ToArray())
            await c.DisposeAsync();
        sipClients.Clear();
        SipLabel.Text = "SIP: выключен";
        PushLabel.Text = "FCM: выключен";
    }

    private void ShowCall(SipClient client, IncomingCall call)
    {
        if (api is null || closed)
            return;
        if(callWindow?.IsTestCall==true){var test=callWindow;callWindow=null;test.Close();}
        if (callWindow?.IsEnded == true)
        {
            callWindow.Close();
            callWindow = null;
        }

        client.InputDevice = (MicrophoneBox.SelectedItem as DeviceItem)?.Id ?? -1;
        client.OutputDevice = (SpeakerBox.SelectedItem as DeviceItem)?.Id ?? -1;
        if (callWindow is { IsVisible: true })
        {
            if (callWindow.CallId == call.Id || callWindow.DoorId == call.Door.Id)
            {
                callWindow.Attach(client);
                return;
            }

            StatusLabel.Text = "Другой входящий звонок";
            return;
        }

        callWindow = new CallWindow(api, call.Door, client, call.Id,speakerDevice:(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1);
        TrackCall(callWindow);
        callWindow.Show();
        callWindow.Activate();
    }

    private void TrackCall(CallWindow window)
    {
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(callWindow, window))
                callWindow = null;
        };
    }

    private void HandlePush(JsonNode? payload)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (api is null || closed || ListenBox.IsChecked != true)
                return;
            var data = payload?["data"] ?? payload;
            var type = Json.Str(data, "PushType");
            var id = Json.Str(data, "Call-ID", "callId") ?? "";
            if (type == "CALL_INCOMING")
            {
                var doorId = Json.Str(data, "AccessControlId");
                var placeId = Json.Str(data, "PlaceId");
                var client = sipClients.FirstOrDefault(c => c.Door.Id == doorId && c.Door.PlaceId == placeId);
                if (client is null)
                    return;
                if (Json.Time(data?["CallInvalidated"])is { } until && until < DateTimeOffset.UtcNow)
                    return;
                await BusyAsync(() => client.RegisterOnRingAsync(id), "Подключение входящего звонка…");
                if (callWindow is null)
                {
                    callWindow = new CallWindow(api, client.Door, null, id,speakerDevice:(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1);
                    TrackCall(callWindow);
                    callWindow.Show();
                    callWindow.Activate();
                }
            }
            else if (type is not null && type.StartsWith("CALL_END", StringComparison.Ordinal) && callWindow?.CallId == id)
            {
                var client = callWindow.Client;
                callWindow.End("Принято на другом устройстве");
                if (client is not null)
                    await client.HangupAsync();
            }
        });
    }

    private void HandleStomp(string type, JsonNode? payload)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (type == "placeEvent" && EventsPanel.Visibility == Visibility.Visible)
                await BusyAsync(LoadEventsAsync, "Обновление событий…");
            var eventName = Json.Str(payload, "PushType", "eventTypeName", "type") ?? type;
            if (eventName is "CALL_INCOMING" or "incomingCall" or "IncomingCallEvent")
            {
                var place = Json.Str(payload, "PlaceId", "placeId");
                var id = Json.Str(payload, "AccessControlId", "accessControlId") ?? Json.Str(payload?["source"], "id");
                var door = allDoors.FirstOrDefault(d => d.Id == id && (place is null || d.PlaceId == place));
                if (door is not null && api is not null && ListenBox.IsChecked == true)
                {
                    var client = sipClients.FirstOrDefault(c => c.Door.Id == door.Id);
                    if (client?.CurrentCall is { } call)
                        ShowCall(client, call);
                    else if (callWindow is null)
                    {
                        callWindow = new CallWindow(api, door, null, Json.Str(payload, "Call-ID", "callId") ?? Guid.NewGuid().ToString(),speakerDevice:(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1);
                        TrackCall(callWindow);
                        callWindow.Show();
                    }
                }
            }
        });
    }

    private async void LogoutClick(object sender, RoutedEventArgs e)
    {
        if (api is null)
            return;
        audioCheckCancellation?.Cancel();audioCheckPlayback?.Dispose();audioCheckPlayback=null;
        loading = true;
        ListenBox.IsChecked = false;
        loading = false;
        await BusyAsync(async () =>
        {
            await StopSipAsync();
            if (stomp is not null)
            {
                await stomp.DisposeAsync();
                stomp = null;
            }

            callWindow?.Close();
            if(video is not null)await video.StopAsync();
            videoRenewal?.Stop();
            api.Logout();
            HomePanel.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;
            LogoutButton.Visibility = Visibility.Collapsed;
            ConnectionLabel.Text = "Не выполнен вход";
        }, "Выход…");
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if(!closed&&!exitRequested&&trayIcon is not null&&CloseToTrayBox.IsChecked==true){e.Cancel=true;await HideToTrayAsync();return;}
        videoLayoutTimer?.Stop();
        archiveCts?.Cancel();
        if (closed){DisposeTray();return;}
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        IsEnabled = false;
        try
        {
            callWindow?.Close();
            audioCheckCancellation?.Cancel();audioCheckPlayback?.Dispose();audioCheckPlayback=null;
            videoRenewal?.Stop();
            await StopSipAsync();
            if (stomp is not null)
                await stomp.DisposeAsync();
            if(video is not null)await video.DisposeAsync();
            api?.Dispose();
        }
        finally
        {
            closed = true;
            DisposeTray();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private async Task SmokeAsync()
    {
        var root = Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR") ?? Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(root);
        await Task.Delay(300);
        Capture(Path.Combine(root, "login.png"));
        loading = true;
        HomePanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        PlaceBox.ItemsSource = new[]
        {
            new Place("demo", "Пример адреса · дом 12", new())
        };
        PlaceBox.SelectedIndex = 0;
        DoorBox.ItemsSource = new[]
        {
            new Door("demo", "demo", "Подъезд 1", "SIP", "demo", null, "ACCESS_CONTROL", true, true, 10800, new())
        };
        DoorBox.SelectedIndex = 0;
        loading = false;
        await UpdateSelectionAsync();
        SelectNavigation("camera");
        ConnectionLabel.Text = "Режим проверки интерфейса";
        VideoPlaceholder.Text = "Камера домофона";
        await Task.Delay(300);
        Capture(Path.Combine(root, "home.png"));
        var smokeWidth=Width;var smokeHeight=Height;Width=1080;Height=740;ApplyVideoLayout();await Task.Delay(200);Capture(Path.Combine(root,"camera-small.png"));
        ExpandVideoClick(this,new());await Task.Delay(200);Capture(Path.Combine(root,"camera-expanded.png"));
        var buttonPosition=ExpandVideoButton.TranslatePoint(new Point(0,0),VideoToolbar);
        await File.WriteAllTextAsync(Path.Combine(root,"toolbar-layout.json"),new JsonObject{["toolbarWidth"]=VideoToolbar.ActualWidth,["actionsWidth"]=ToolbarActionsGrid.ActualWidth,["expandButtonX"]=buttonPosition.X,["expandButtonWidth"]=ExpandVideoButton.ActualWidth}.ToJsonString());
        if(buttonPosition.X+ExpandVideoButton.ActualWidth>VideoToolbar.ActualWidth)throw new InvalidOperationException("Video controls exceed toolbar bounds");
        ExpandVideoClick(this,new());Width=smokeWidth;Height=smokeHeight;ApplyVideoLayout();
        SelectNavigation("events");
        VideoPanel.Visibility = Visibility.Collapsed;
        EventsPanel.Visibility = Visibility.Visible;
        SelectNavigation("events");
        EventsGrid.ItemsSource = new[]
        {
            new EventItem("07.10.2026 18:24:11", "Входящий звонок в домофон", new("demo", "Входящий звонок", DateTimeOffset.Now, null, new())),
            new EventItem("07.10.2026 18:21:04", "Дверь открыта из приложения", new("demo2", "Дверь открыта", DateTimeOffset.Now, null, new()))
        };
        EventsCount.Text="Событий: 2";
        await Task.Delay(300);
        Capture(Path.Combine(root, "events.png"));EventsProgress.Visibility=Visibility.Visible;await Task.Delay(200);Capture(Path.Combine(root,"events-loading.png"));EventsProgress.Visibility=Visibility.Collapsed;
        ArchiveTabClick(this,new());
        var demoEvents=new List<HistoryEvent>();
        for(int day=0;day<3;day++)
        {
            demoEvents.Add(new("demo-call-"+day,"Входящий звонок",new DateTimeOffset(DateTime.Now.Date.AddDays(-day).AddHours(9)),null,new()));
            demoEvents.Add(new("demo-motion-"+day,"Движение у входа",new DateTimeOffset(DateTime.Now.Date.AddDays(-day).AddHours(8)),null,new()));
        }
        SetArchiveDay(0);RenderArchive(demoEvents.Where(x=>x.Time!.Value.LocalDateTime.Date==DateTime.Now.Date).ToList());ArchiveStatus.Text="Событий: 2";
        ArchiveProgress.Visibility=Visibility.Visible;await Task.Delay(200);Capture(Path.Combine(root,"archive-loading.png"));ArchiveProgress.Visibility=Visibility.Collapsed;
        await Task.Delay(200);
        Capture(Path.Combine(root,"archive.png"));
        var archiveSmokeWidth=Width;var archiveSmokeHeight=Height;Width=1080;Height=740;await Task.Delay(200);Capture(Path.Combine(root,"archive-small.png"));Width=archiveSmokeWidth;Height=archiveSmokeHeight;
        SetArchiveDay(1);RenderArchive(demoEvents.Where(x=>x.Time!.Value.LocalDateTime.Date==DateTime.Now.Date.AddDays(-1)).ToList());Capture(Path.Combine(root,"archive-yesterday.png"));SetArchiveDay(0);
        SetArchiveDay(2);var midnightSample=new DateTimeOffset(DateTime.Now.Date.AddDays(-2).AddHours(23).AddMinutes(59).AddSeconds(48));
        RenderArchive(new(){new("demo-midnight","Запись камеры",midnightSample,null,new(){["Time"]=midnightSample.ToUnixTimeSeconds(),["Duration"]=30}),new("demo-normal","Запись камеры",midnightSample.AddMinutes(-1),null,new(){["Time"]=midnightSample.AddMinutes(-1).ToUnixTimeSeconds(),["Duration"]=30})});Capture(Path.Combine(root,"archive-midnight.png"));SetArchiveDay(0);
        SelectNavigation("keys");ArchivePanel.Visibility=Visibility.Collapsed;KeysList.ItemsSource=new[]{new AccessKeyInfo("Семейный ключ","DEMO-001","Активен","Подъезд 1"),new AccessKeyInfo("Запасной ключ","DEMO-002","Активен","Подъезд 1")};KeysList.SelectedIndex=0;KeysStatus.Text="Ключей: 2";Capture(Path.Combine(root,"keys.png"));KeysProgress.Visibility=Visibility.Visible;await Task.Delay(200);Capture(Path.Combine(root,"keys-loading.png"));KeysProgress.Visibility=Visibility.Collapsed;
        SelectNavigation("payments");ProfileName.Text="Пример аккаунта";ProfileContract.Text="Договор: пример";ProfileAddress.Text="Пример адреса · дом 12";FinanceProvider.Text="Цифрал-Сервис";FinanceBalance.Text="0,00 ₽";FinanceAmount.Text="100,00 ₽";FinanceDue.Text="Срок оплаты: 15.10.2026";FinanceStatus.Text="Услуга доступна";Capture(Path.Combine(root,"payments.png"));
        SettingsClick(this,new());
        await Task.Delay(200);
        Capture(Path.Combine(root,"settings.png"));
        AuthTabs.SelectedIndex=1;
        HomePanel.Visibility=Visibility.Collapsed;
        LoginPanel.Visibility=Visibility.Visible;
        await Task.Delay(200);
        Capture(Path.Combine(root,"sms-login.png"));
        using (var demoApi = new DomruApi(Guid.NewGuid().ToString()))
        {
            var demoCall = new CallWindow(demoApi, (Door)DoorBox.SelectedItem, null, "preview")
            {
                PreviewMode = true
            };
            demoCall.Show();
            await Task.Delay(300);
            demoCall.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)demoCall.ActualWidth, (int)demoCall.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(demoCall);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var file = File.Create(Path.Combine(root, "call.png")))
                encoder.Save(file);
            demoCall.Close();
        }

        closed = true;
        videoLayoutTimer?.Stop();
        if(video is not null)await video.DisposeAsync();
        Close();
    }

    private void Capture(string path)
    {
        UpdateLayout();
        var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(this);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path);
        png.Save(file);
    }
}







