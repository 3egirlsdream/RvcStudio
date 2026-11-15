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
    private readonly Image _cover=new(){Stretch=Stretch.UniformToFill,VerticalAlignment=VerticalAlignment.Top};
    private readonly Border _coverFrame=new(){CornerRadius=new CornerRadius(12),ClipToBounds=true,Background=Brush.Parse("#151917")};
    private readonly TextBlock _name=new(){FontSize=20,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#EEF2EE"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _badge=new(){FontSize=11,Foreground=Brush.Parse("#DDF7BC"),VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _state=new(){FontSize=12,Foreground=Brush.Parse("#A7AFA9"),TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _message=new(){FontSize=12,Foreground=Brush.Parse("#FFA29D"),TextWrapping=TextWrapping.Wrap,IsVisible=false};
    private readonly Button _listen=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Button _use=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Button _manage=new(){Content="管理音色",HorizontalAlignment=HorizontalAlignment.Stretch,IsVisible=false};
    private readonly ProgressBar _progress=new(){Minimum=0,Maximum=100,Height=4,IsVisible=false};
    private readonly Button _cancel=new(){Content="取消下载",IsVisible=false};
    private readonly Button _downloads=new(){Width=28,Height=28,MinHeight=28,Padding=new Thickness(7),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(8),Background=Brush.Parse("#E6202522"),BorderBrush=Brush.Parse("#414B45"),IsVisible=false};
    private CancellationTokenSource? _download;
    private bool _busy,_disposed;
    private string? _coverRevision;

    public StoreItemCard(MainViewModel vm,Window owner,StoreItem item,CancellationToken lifetime,Func<Task> reload)
    {
        _vm=vm;_owner=owner;_item=item;_life=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        VerticalAlignment=VerticalAlignment.Top;
        RenderOptions.SetBitmapInterpolationMode(_cover,BitmapInterpolationMode.HighQuality);
        _listen.MinHeight=38;
        var content=new StackPanel {Spacing=0};
        var artwork=new Grid();artwork.Children.Add(_cover);artwork.Children.Add(_downloads);_coverFrame.Child=artwork;content.Children.Add(_coverFrame);
        var text=new StackPanel {Margin=new Thickness(2,10,2,0),Spacing=5};
        var title=new Grid {ColumnDefinitions=new ColumnDefinitions("Auto,Auto,*"),ColumnSpacing=10};
        title.Children.Add(_name);
        var badge=new Border {Child=_badge,Background=Brush.Parse("#1E2B1A"),BorderBrush=Brush.Parse("#344A2A"),BorderThickness=new Thickness(1),Padding=new Thickness(8,2),CornerRadius=new CornerRadius(20),VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumn(badge,1);title.Children.Add(badge);text.Children.Add(title);text.Children.Add(_state);
        _use.Classes.Add("primary");
        var actions=new Grid {ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=10,Margin=new Thickness(0,5,0,0)};
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
        _downloads.Content=StoreVisual.Icon("dots-three",16);ToolTip.SetTip(_downloads,"管理下载");
        Avalonia.Automation.AutomationProperties.SetName(_downloads,"管理下载");
        _downloads.Flyout=new MenuFlyout {Items={repair,remove}};
        _manage.Classes.Add("store-link");
        _manage.Click+=async (_,_)=>{await new StoreUploadWindow(vm,_item).ShowDialog(owner);if(!_disposed)await reload();};text.Children.Add(_manage);
        content.Children.Add(text);Child=content;
        _listen.Click+=async (_,_)=>await Listen();_use.Click+=async (_,_)=>await Use();_cancel.Click+=(_,_)=>_download?.Cancel();
        StoreMedia.PreviewChanged+=PreviewChanged;
        Update(item,false);
    }
    public void SetWidth(double width)
    {
        // Crop only the lower part of a square portrait, keeping every face intact.
        _cover.Width=width;_cover.Height=width;_coverFrame.Height=width*0.82;
        _name.MaxWidth=Math.Max(80,width-66);
    }
    public void Update(StoreItem item,bool management)
    {
        _item=item;_name.Text=item.Name;_badge.Text=item.Badge;_manage.IsVisible=management;
        ToolTip.SetTip(_name,item.Name);ToolTip.SetTip(_coverFrame,item.Description);
        if(_coverRevision!=item.Revision || _cover.Source is null)
        {_coverRevision=item.Revision;_cover.Source=StoreMedia.Cover(item.Id);_=FetchCover();}
        Refresh();
    }
    public void Refresh()
    {
        if(_disposed)return;
        var downloaded=_vm.Store.IsDownloaded(_item);
        _downloads.IsVisible=!_item.IsBuiltin&&downloaded;_downloads.IsEnabled=!_busy;
        _state.Text=_item.IsBuiltin?"内置":downloaded?"已下载 · 离线可用":_item.State=="Preview"?"本地试听 · 未取得上架信息":string.IsNullOrEmpty(_item.Revision)||_item.State=="Draft"?"尚未上架":_item.Size>0?UpdatePackage.FormatSize(_item.Size):"可下载";
        var unavailable=!_item.IsBuiltin&&!downloaded&&(string.IsNullOrEmpty(_item.Revision)||_item.State is "Draft" or "Unpublished" or "Preview");
        _use.Content=unavailable?(_item.State=="Preview"?"暂不可用":"未上架"):_item.IsBuiltin||downloaded?"使用":!_vm.Account.IsAuthenticated?"登录使用":!_vm.Store.CanUse(_item.Access)?"开通会员":"下载使用";
        _use.IsEnabled=!_busy&&!unavailable;
        PreviewChanged();
    }
    private void PreviewChanged()
    {
        if(_disposed)return;
        var playing=StoreMedia.PlayingPreviewId==_item.Id;
        var loading=StoreMedia.LoadingPreviewId==_item.Id;
        _listen.Content=StoreVisual.ListenContent(playing,loading);
        ToolTip.SetTip(_listen,loading?"正在加载试听，点击取消":playing?"停止试听":"试听此音色");
        Avalonia.Automation.AutomationProperties.SetName(_listen,(loading?"取消加载 ":playing?"停止试听 ":"试听 ")+_item.Name);
        Avalonia.Automation.AutomationProperties.SetName(_use,_use.Content+" "+_item.Name);
    }
    private void Message(string message){if(!_disposed){_message.Text=message;_message.IsVisible=true;}}
    private async Task Listen()
    {
        if(StoreMedia.PlayingPreviewId==_item.Id||StoreMedia.LoadingPreviewId==_item.Id){StoreMedia.Stop();return;}
        try {await StoreMedia.PreviewAsync(_vm.Store,_item,_life.Token);}
        catch(OperationCanceledException) { }
        catch {Message("试听暂不可用，请稍后重试。");}
        finally {if(!_disposed)PreviewChanged();}
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
        if(string.IsNullOrEmpty(_item.Revision)||StoreMedia.HasBundledRevision(_item))return;
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
