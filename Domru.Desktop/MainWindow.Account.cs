using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Domru.Desktop.Core;
namespace Domru.Desktop;
public partial class MainWindow
{
    private string? accountPaymentLink;
    private int accountLoadVersion,keysLoadVersion;
    private async Task ShowServicePageAsync(string page)
    {
        var navigation=BeginNavigation(page);
        ExitFullscreen();videoVersion++;videoRenewal?.Stop();if(video is not null)await video.StopAsync();
        if(navigation!=navigationVersion)return;
        VideoPanel.Visibility=EventsPanel.Visibility=ArchivePanel.Visibility=Visibility.Collapsed;
        SelectNavigation(page);archiveMode=false;
    }
    private async void KeysClick(object sender,RoutedEventArgs e){await ShowServicePageAsync("keys");await LoadKeysAsync();}
    private async void PaymentsClick(object sender,RoutedEventArgs e){await ShowServicePageAsync("payments");await LoadAccountAsync();}
    private async void KeysRefreshClick(object sender,RoutedEventArgs e)=>await LoadKeysAsync();
    private async void AccountRefreshClick(object sender,RoutedEventArgs e)=>await LoadAccountAsync();
    private async Task LoadKeysAsync()
    {
        if(api is null||Place is not {} place)return;
        var selectedCode=(KeysList.SelectedItem as AccessKeyInfo)?.Code;var version=++keysLoadVersion;KeysProgress.Visibility=Visibility.Visible;KeysRefresh.IsEnabled=false;KeysEmptyText.Visibility=Visibility.Collapsed;KeysList.ItemsSource=null;KeysStatus.Text="Загрузка ключей…";
        try
        {
            var keys=AccountServices.ParseKeys(await api.AccessKeysAsync(place.Id));
            if(version!=keysLoadVersion||Place?.Id!=place.Id||closing)return;
            KeysList.ItemsSource=keys;KeysEmptyText.Visibility=keys.Count==0?Visibility.Visible:Visibility.Collapsed;if(selectedCode is not null)KeysList.SelectedItem=keys.FirstOrDefault(x=>x.Code==selectedCode);KeysStatus.Text=keys.Count==0?"К этому адресу ключи не привязаны":$"Ключей: {keys.Count}";
        }
        catch(Exception){if(version==keysLoadVersion&&Place?.Id==place.Id)KeysStatus.Text="Не удалось загрузить ключи. Нажмите «Обновить», чтобы повторить.";}
        finally{if(version==keysLoadVersion){KeysProgress.Visibility=Visibility.Collapsed;KeysRefresh.IsEnabled=true;}}
    }
    private static async Task<JsonNode?> ReadAccountPartAsync(Func<Task<JsonNode?>> read)
    {
        try{return Json.Data(await read());}catch{return null;}
    }
    private async Task LoadAccountAsync()
    {
        if(api is null||Place is not {} place)return;
        var version=++accountLoadVersion;accountPaymentLink=null;AccountPayButton.IsEnabled=false;
        ProfileAddress.Text=place.Name;ProfileName.Text="Загрузка…";ProfileContract.Text="";
        FinanceBalance.Text=FinanceAmount.Text="—";FinanceDue.Text=FinanceStatus.Text="";AccountLoadStatus.Text="Загрузка данных аккаунта…";
        var results=await Task.WhenAll(ReadAccountPartAsync(()=>api.FinanceAsync(place.Id)),ReadAccountPartAsync(()=>api.PaymentInfoAsync(place.Id)),ReadAccountPartAsync(api.ProfileAsync));
        if(version!=accountLoadVersion||Place?.Id!=place.Id||closing)return;
        var finance=results[0];var payment=results[1];var profile=results[2]?["subscriber"]??results[2];
        var link=Json.Str(payment,"paymentLink")??Json.Str(finance,"paymentLink");accountPaymentLink=AccountServices.IsHttps(link)?link:null;
        ProfileName.Text=Json.Str(profile,"nickName") is {Length:>0} nickname?nickname:Json.Str(profile,"name") is {Length:>0} name?name:"Абонент Умный Дом.ру";
        var contract=Json.Str(profile,"accountId","contractNumber");ProfileContract.Text=contract is null?"Номер договора недоступен":"Договор: "+contract;
        var company=Json.Str(finance?["company"],"name")??Json.Str(payment?["managementCompany"],"name")??AccountServices.Provider(link);
        FinanceProvider.Text=company;FinanceBalance.Text=AccountServices.Money(finance,"balance");FinanceAmount.Text=AccountServices.Money(finance,"amountSum");
        FinanceDue.Text="Срок оплаты: "+AccountServices.Date(finance,"targetDate");
        FinanceStatus.Text=finance is null?"Финансовые данные недоступны":Json.Bool(finance,"blocked",false)?"Услуга заблокирована":"Услуга доступна";
        AccountPayButton.IsEnabled=accountPaymentLink is not null;
        AccountLoadStatus.Text=finance is null?"Откройте официальный кабинет провайдера, чтобы проверить начисления.":"Данные по выбранному адресу. Оплата открывается на сайте обслуживающей компании.";
    }
    private void AccountPayClick(object sender,RoutedEventArgs e)=>OpenProviderLink(accountPaymentLink);
    private void ProviderLinkClick(object sender,RoutedEventArgs e){if(sender is Button {Tag:string link})OpenProviderLink(link);}
    private void OpenProviderLink(string? link)
    {
        if(!AccountServices.IsHttps(link))return;
        try{Process.Start(new ProcessStartInfo(link!){UseShellExecute=true});}catch{StatusLabel.Text="Не удалось открыть браузер. Проверьте браузер по умолчанию в Windows.";}
    }
    private async Task VerifyServicesAsync()
    {
        await ShowServicePageAsync("keys");await LoadKeysAsync();var count=KeysList.Items.Count;
        await ShowServicePageAsync("payments");await LoadAccountAsync();
        if(count==0||!AccountPayButton.IsEnabled||FinanceBalance.Text=="Нет данных")throw new InvalidOperationException("Сервисы аккаунта не загрузились");
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??System.IO.Path.Combine(Environment.CurrentDirectory,"artifacts");System.IO.Directory.CreateDirectory(root);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(root,"account-verification.json"),new JsonObject{["keys"]=count,["financeAvailable"]=FinanceBalance.Text!="Нет данных",["paymentLinkAvailable"]=AccountPayButton.IsEnabled,["paymentProvider"]=AccountServices.Provider(accountPaymentLink),["profileAvailable"]=!ProfileContract.Text.Contains("недоступен")}.ToJsonString(new(){WriteIndented=true}));Close();
    }
}
