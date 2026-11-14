using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RvcStudio.App;

internal sealed class StoreItemCard : Border,IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Window _owner;
    private readonly CancellationTokenSource _life;
    private StoreItem _item;
    private readonly Image _cover=new(){Width=212,Height=194,Stretch=Stretch.UniformToFill};
    private readonly TextBlock _name=new(){FontSize=19,FontWeight=FontWeight.SemiBold};
    private readonly TextBlock _meta=new(){FontSize=11,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _state=new(){FontSize=11,Foreground=Brush.Parse("#B7F36B"),TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _message=new(){FontSize=11,TextWrapping=TextWrapping.Wrap,IsVisible=false};
    private readonly Button _listen=new(){Content="▶ 试听",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Button _use=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Button _manage=new(){Content="管理音色",HorizontalAlignment=HorizontalAlignment.Stretch,IsVisible=false};
    private readonly ProgressBar _progress=new(){Minimum=0,Maximum=100,Height=5,IsVisible=false};
    private readonly Button _cancel=new(){Content="取消下载",IsVisible=false};
    private readonly Button _downloads=new(){Content="管理下载",HorizontalAlignment=HorizontalAlignment.Stretch,IsVisible=false};
    private CancellationTokenSource? _download;
    private bool _busy,_disposed;
    private string? _coverRevision;

    public StoreItemCard(MainViewModel vm,Window owner,StoreItem item,CancellationToken lifetime,Func<Task> reload)
    {
        _vm=vm;_owner=owner;_item=item;_life=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Width=212;Margin=new Thickness(7);CornerRadius=new CornerRadius(12);ClipToBounds=true;
        Background=Brush.Parse("#19201A");BorderBrush=Brush.Parse("#303D31");BorderThickness=new Thickness(1);
        var content=new StackPanel {Spacing=7};content.Children.Add(_cover);
        var text=new StackPanel {Margin=new Thickness(12,4,12,12),Spacing=8};
        text.Children.Add(_name);text.Children.Add(_meta);text.Children.Add(_state);
        var actions=new Grid {ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=7};
        actions.Children.Add(_listen);Grid.SetColumn(_use,1);actions.Children.Add(_use);text.Children.Add(actions);
        text.Children.Add(_progress);text.Children.Add(_cancel);text.Children.Add(_message);
        var repair=new MenuItem {Header="重新下载 / 修复"};repair.Click+=async (_,_)=>await Repair();
        var remove=new MenuItem {Header="移除此下载"};remove.Click+=(_,_)=>
        {
            if(_busy)return;
            if(_vm.ActiveStoreId==_item.Id){Message("请先使用其他音色，再移除此下载。");return;}
            try {_vm.Store.DeleteDownload(_item);_vm.RefreshModelChoices();Refresh();Message("已移除此账号的下载授权。");}
            catch {Message("移除失败，请稍后重试。");}
        };
        _downloads.Flyout=new MenuFlyout {Items={repair,remove}};text.Children.Add(_downloads);
        _manage.Click+=async (_,_)=>{await new StoreUploadWindow(vm,_item).ShowDialog(owner);if(!_disposed)await reload();};text.Children.Add(_manage);
        content.Children.Add(text);Child=content;
        _listen.Click+=async (_,_)=>await Listen();_use.Click+=async (_,_)=>await Use();_cancel.Click+=(_,_)=>_download?.Cancel();
        StoreMedia.PreviewChanged+=PreviewChanged;
        Update(item,false);
    }
    public void Update(StoreItem item,bool management)
    {
        _item=item;_name.Text=item.Name;_meta.Text=item.Badge+" · "+item.Details;_manage.IsVisible=management;
        if(_coverRevision!=item.Revision || _cover.Source is null)
        {_coverRevision=item.Revision;_cover.Source=StoreMedia.Cover(item.Id);_=FetchCover();}
        Refresh();
    }
    public void Refresh()
    {
        if(_disposed)return;
        var downloaded=_vm.Store.IsDownloaded(_item);
        _downloads.IsVisible=!_item.IsBuiltin&&downloaded;_downloads.IsEnabled=!_busy;
        _state.Text=_item.IsBuiltin?"内置可用":downloaded?"已下载":_item.State=="Preview"?"本地试听 · 未取得上架信息":string.IsNullOrEmpty(_item.Revision)||_item.State=="Draft"?"尚未上架":"可下载";
        var unavailable=!_item.IsBuiltin&&!downloaded&&(string.IsNullOrEmpty(_item.Revision)||_item.State is "Draft" or "Unpublished" or "Preview");
        _use.Content=unavailable?(_item.State=="Preview"?"暂不可用":"未上架"):_item.IsBuiltin||downloaded?"使用":!_vm.Account.IsAuthenticated?"登录使用":!_vm.Store.CanUse(_item.Access)?"开通会员":"下载使用";
        _use.IsEnabled=!_busy&&!unavailable;
        PreviewChanged();
    }
    private void PreviewChanged(){if(!_disposed)_listen.Content=StoreMedia.PlayingPreviewId==_item.Id?"■ 停止":"▶ 试听";}
    private void Message(string message){if(!_disposed){_message.Text=message;_message.IsVisible=true;}}
    private async Task Listen()
    {
        if(StoreMedia.PlayingPreviewId==_item.Id){StoreMedia.Stop();return;}
        _listen.IsEnabled=false;
        try {await StoreMedia.PreviewAsync(_vm.Store,_item,_life.Token);}
        catch(OperationCanceledException) { }
        catch {Message("试听暂不可用，请稍后重试。");}
        finally {if(!_disposed)_listen.IsEnabled=true;}
    }
    private async Task Use()
    {
        if(_busy)return;
        StoreMedia.Stop();
        _busy=true;Refresh();
        try
        {
            if(!_item.IsBuiltin&&!_vm.Account.IsAuthenticated)
            {await new AccountWindow(_vm.Account).ShowDialog(_owner);if(!_vm.Account.IsAuthenticated)return;}
            if(!_item.IsBuiltin&&!_vm.Store.CanUse(_item.Access))
            {await new MembershipWindow(_vm.Account).ShowDialog(_owner);if(!_vm.Store.CanUse(_item.Access))return;}
            _life.Token.ThrowIfCancellationRequested();
            if(!_item.IsBuiltin&&!_vm.Store.IsDownloaded(_item))
            {
                _download=CancellationTokenSource.CreateLinkedTokenSource(_life.Token);_progress.IsVisible=_cancel.IsVisible=true;
                Message("正在下载并校验…");
                await _vm.Store.DownloadAsync(_item,new Progress<double>(v=>{if(!_disposed)_progress.Value=v;}),_download.Token);
            }
            _life.Token.ThrowIfCancellationRequested();
            await _vm.UseStoreItemAsync(_item);
            var home=_owner.Owner;
            _owner.Close();home?.Activate();
        }
        catch(OperationCanceledException){Message("已取消，点击使用可继续下载。");}
        catch(Exception ex){Message(ex is RvcStudioApiException?ex.Message:"使用失败，可通过管理下载重新下载或修复。");}
        finally
        {
            _download?.Dispose();_download=null;_busy=false;
            if(!_disposed){_progress.IsVisible=_cancel.IsVisible=false;Refresh();}
        }
    }
    private async Task Repair()
    {
        if(_busy)return;_busy=true;Refresh();
        try
        {
            if(!_vm.Store.CanUse(_item.Access))throw new RvcStudioApiException("请先刷新账号会员资格。");
            _download=CancellationTokenSource.CreateLinkedTokenSource(_life.Token);_progress.IsVisible=_cancel.IsVisible=true;
            Message("正在重新下载并校验…");
            await _vm.Store.DownloadAsync(_item,new Progress<double>(v=>{if(!_disposed)_progress.Value=v;}),_download.Token);
            _vm.RefreshModelChoices();Message("已修复，可直接使用。");
        }
        catch(OperationCanceledException){Message("已取消，可再次尝试修复。");}
        catch(Exception ex){Message(ex is RvcStudioApiException?ex.Message:"修复失败，请稍后重试。");}
        finally {_download?.Dispose();_download=null;_busy=false;if(!_disposed){_progress.IsVisible=_cancel.IsVisible=false;Refresh();}}
    }
    private async Task FetchCover()
    {
        if(string.IsNullOrEmpty(_item.Revision))return;
        try
        {
            var bytes=await _vm.Store.AssetAsync(_item.CoverUrl,_life.Token);
            if(!_disposed){using var stream=new MemoryStream(bytes);_cover.Source=new Bitmap(stream);}
        }
        catch { }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;StoreMedia.PreviewChanged-=PreviewChanged;_life.Cancel();_download?.Cancel();_life.Dispose();
    }
}
