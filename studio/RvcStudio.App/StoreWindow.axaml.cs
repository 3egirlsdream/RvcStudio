using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace RvcStudio.App;

public partial class StoreWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _lifetime=new();
    private IReadOnlyList<StoreItem> _items=[];
    private bool _management;
    private readonly Dictionary<string,StoreItemCard> _cards=[];
    public StoreWindow():this(new MainViewModel()) { }
    public StoreWindow(MainViewModel vm)
    {
        InitializeComponent();_vm=vm;
        Opened+=async (_,_)=>await Reload();
        Closed+=(_,_)=>{_lifetime.Cancel();foreach(var card in _cards.Values)card.Dispose();_cards.Clear();StoreMedia.Stop();_lifetime.Dispose();};
    }
    private async Task Reload()
    {
        try
        {
            _items=await _vm.Store.CatalogAsync(_management,_lifetime.Token);
            ManageButton.IsVisible=_vm.Store.CanManage;
            StatusText.Text=string.IsNullOrEmpty(_vm.Store.Status)?(_management?"管理模式：点击商品编辑、上传新版本或上下架。":"会员模型使用最近一次成功登录或刷新得到的资格，离线可使用已授权下载。"): _vm.Store.Status;
            Render();
        }
        catch(OperationCanceledException) { }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void Render()
    {
        Cards.Children.Clear();
        if(_management)
        {
            var add=new Button {Content="＋ 新建音色",Width=212,Height=60,Margin=new Thickness(7)};
            add.Click+=async (_,_)=>{await new StoreUploadWindow(_vm,null).ShowDialog(this);await Reload();};Cards.Children.Add(add);
        }
        foreach(var item in _items.Where(i=>(i.Name.Contains(SearchBox.Text??"",StringComparison.OrdinalIgnoreCase)) && (FreeOnly.IsChecked!=true||i.Access=="Free") && (DownloadedOnly.IsChecked!=true||i.IsBuiltin||_vm.Store.IsDownloaded(i))))
        {
            var key=item.Id+"/"+item.Revision;
            if(!_cards.TryGetValue(key,out var card))_cards[key]=card=new StoreItemCard(_vm,this,item,_lifetime.Token,Reload);
            card.Update(item,_management);Cards.Children.Add(card);
        }
    }
    private async void Refresh_Click(object? s,RoutedEventArgs e)
    {
        try {if(_vm.Account.IsAuthenticated)await _vm.Account.RefreshAsync(_lifetime.Token);}
        catch(OperationCanceledException) {return;}
        catch(Exception) { /* Preserve the last successful membership snapshot when offline. */ }
        await Reload();
    }
    private async void Manage_Click(object? s,RoutedEventArgs e){_management=!_management;await Reload();}
    private async void Account_Click(object? s,RoutedEventArgs e)
    {await new AccountWindow(_vm.Account).ShowDialog(this);await new MembershipWindow(_vm.Account).ShowDialog(this);await Reload();}
    private void Filter_Changed(object? s,RoutedEventArgs e){if(_vm is not null)Render();}
}
