using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RvcStudio.App;

public partial class StoreWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _lifetime=new();
    private IReadOnlyList<StoreItem> _items=[];
    private bool _management,_closed;
    private string _filter="All";
    private readonly Dictionary<string,StoreItemCard> _cards=[];
    public StoreWindow():this(new MainViewModel()) { }
    public StoreWindow(MainViewModel vm)
    {
        InitializeComponent();_vm=vm;
        SearchBox.InnerLeftContent=StoreVisual.Icon("magnifying-glass",17,"#78817B");
        RefreshButton.Content=StoreVisual.Icon("arrow-clockwise",17);
        _vm.Account.Changed+=AccountChanged;
        UpdateHeader();
        SizeChanged+=(_,_)=>UpdateSpacing();
        Opened+=async (_,_)=>await Reload();
        Closed+=(_,_)=>{
            _closed=true;_vm.Account.Changed-=AccountChanged;_lifetime.Cancel();
            foreach(var card in _cards.Values)card.Dispose();_cards.Clear();
            StoreMedia.Stop();_lifetime.Dispose();
        };
    }
    private void UpdateSpacing()
    {
        var margin=Bounds.Width<1000?28:44;
        Navigation.Margin=new Thickness(margin,0);
        Heading.Margin=new Thickness(margin,25,margin,8);
        Filters.Margin=new Thickness(margin,0,margin,0);
        StatusBanner.Margin=new Thickness(margin,12,margin,0);
        GalleryScroll.Margin=new Thickness(margin,24,margin-6,16);
        Subtitle.IsVisible=Bounds.Width>=1040;
        SearchBox.Width=Bounds.Width<1000?224:292;
    }
    private void AccountChanged()=>Dispatcher.UIThread.Post(()=>{
        if(_closed)return;
        if(!_vm.Store.CanManage)_management=false;
        UpdateHeader();Render();
    });
    private void UpdateHeader()
    {
        ManageButton.IsVisible=ManageDivider.IsVisible=_vm.Store.CanManage;
        ManageButton.Content=_management?"退出管理":"商城管理";AddButton.IsVisible=_management;
        var content=new StackPanel {Orientation=Orientation.Horizontal,Spacing=7};
        content.Children.Add(new TextBlock {Text=_vm.Account.Account?.DisplayName??"登录 / 注册",FontSize=13,Foreground=Brush.Parse("#DDE3DE"),VerticalAlignment=VerticalAlignment.Center});
        content.Children.Add(StoreVisual.Icon("caret-down",12));AccountButton.Content=content;
        ToolTip.SetTip(AccountButton,_vm.Account.IsAuthenticated?"账号设置 / 会员中心":"登录或注册 RVC Studio 账号");
    }
    private async Task Reload()
    {
        RefreshButton.IsEnabled=false;
        try {
            var items=await _vm.Store.CatalogAsync(_management,_lifetime.Token);
            if(_closed)return;_items=items;UpdateHeader();
            // Start the small previews before cover requests can occupy the connection slots.
            _=StoreMedia.PreloadPreviewsAsync(_vm.Store,_items,_lifetime.Token);
            StatusText.Text=_vm.Store.Status;StatusBanner.IsVisible=!string.IsNullOrEmpty(_vm.Store.Status);Render();
        }
        catch(OperationCanceledException) { }
        catch(Exception) {
            if(_closed)return;
            StatusText.Text="商城暂时无法刷新，请稍后重试。";StatusBanner.IsVisible=true;Render();
        }
        finally {if(!_closed)RefreshButton.IsEnabled=true;}
    }
    private void Render()
    {
        Cards.Children.Clear();
        var query=(SearchBox.Text??"").Trim();
        var items=_items.Where(i=>i.Name.Contains(query,StringComparison.OrdinalIgnoreCase) && (_filter switch {
            "Free"=>i.Access=="Free","Member"=>i.Access=="Member",
            "Downloaded"=>i.IsBuiltin||_vm.Store.IsDownloaded(i),_=>true
        })).OrderBy(i=>i.IsBuiltin?0:1).ToList();
        foreach(var item in items) {
            var key=item.Id+"/"+item.Revision;
            if(!_cards.TryGetValue(key,out var card))_cards[key]=card=new StoreItemCard(_vm,this,item,_lifetime.Token,Reload);
            card.Update(item,_management);Cards.Children.Add(card);
        }
        EmptyState.IsVisible=items.Count==0;
        if(items.Count==0) {
            EmptyTitle.Text=_items.Count==0?"暂时没有上架音色":"没有找到符合条件的音色";
            EmptyDescription.Text=query.Length>0?"换个名称试试，或查看全部音色。":_filter=="Downloaded"?"下载喜欢的会员音色后，可在这里直接使用。":"可以刷新商城，或查看其他音色。";
            ResetFilterButton.IsVisible=_filter!="All"||query.Length>0;
        }
    }
    private void SelectFilter(string filter)
    {
        _filter=filter;
        foreach(var button in new[]{AllFilter,FreeFilter,MemberFilter,DownloadedFilter})button.Classes.Set("selected",(string?)button.Tag==filter);
        BrowseNav.Classes.Set("selected",filter!="Downloaded");DownloadsNav.Classes.Set("selected",filter=="Downloaded");
        GalleryScroll.Offset=new Vector();Render();
    }
    private async void Refresh_Click(object? s,RoutedEventArgs e)
    {
        RefreshButton.IsEnabled=false;
        try {if(_vm.Account.IsAuthenticated)await _vm.Account.RefreshAsync(_lifetime.Token);}
        catch(OperationCanceledException) {return;}
        catch(Exception) { /* Offline refresh preserves the last successful membership snapshot. */ }
        if(!_closed)await Reload();
    }
    private async void Manage_Click(object? s,RoutedEventArgs e){_management=!_management;await Reload();}
    private async void Add_Click(object? s,RoutedEventArgs e){await new StoreUploadWindow(_vm,null).ShowDialog(this);if(!_closed)await Reload();}
    private async void Account_Click(object? s,RoutedEventArgs e)
    {
        var account=new AccountWindow(_vm.Account);await account.ShowDialog(this);
        if(account.OpenMembershipRequested&&_vm.Account.IsAuthenticated)await new MembershipWindow(_vm.Account).ShowDialog(this);
        if(!_closed)await Reload();
    }
    private void Back_Click(object? s,RoutedEventArgs e){var home=Owner;Close();home?.Activate();}
    private async void Browse_Click(object? s,RoutedEventArgs e)
    {
        SearchBox.Text="";SelectFilter("All");
        if(_management){_management=false;await Reload();}
    }
    private void Downloads_Click(object? s,RoutedEventArgs e)=>SelectFilter("Downloaded");
    private void Filter_Click(object? s,RoutedEventArgs e){if(s is Button {Tag:string filter})SelectFilter(filter);}
    private void Search_Changed(object? s,TextChangedEventArgs e){if(_vm is not null)Render();}
}
