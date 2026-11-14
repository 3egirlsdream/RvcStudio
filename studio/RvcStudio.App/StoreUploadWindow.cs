using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace RvcStudio.App;

public sealed class StoreUploadWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly StoreItem _item;
    private readonly TextBox _name=new();
    private readonly TextBox _description=new(){AcceptsReturn=true,MinHeight=65,TextWrapping=TextWrapping.Wrap};
    private readonly CheckBox _free=new(){Content="免费资源"};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Image _coverPreview=new(){Height=170,Stretch=Stretch.UniformToFill};
    private readonly Button _upload=new(){Content="上传草稿"};
    private readonly Button _publish=new(){Content="发布已上传版本",IsEnabled=false};
    private readonly Button _retry=new(){Content="重试上传任务",IsVisible=false};
    private readonly CancellationTokenSource _life=new();
    private readonly Dictionary<string,TextBlock> _labels=[];
    private string? _model,_index,_cover,_preview,_parameters;
    private StoreJobStatus? _job;
    private bool _closed;
    public StoreUploadWindow(MainViewModel vm,StoreItem? item)
    {
        _vm=vm;_item=item is null?new StoreItem():JsonSerializer.Deserialize<StoreItem>(JsonSerializer.Serialize(item,StoreCrypto.Json),StoreCrypto.Json)!;
        Title="商城管理 · "+(item?.Name??"新建音色");Width=640;Height=850;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24),Spacing=10};
        panel.Children.Add(new TextBlock {Text=item is null?"新建音色":"编辑并上传新版本",FontSize=23,FontWeight=FontWeight.SemiBold});
        _name.Text=_item.Name;_description.Text=_item.Description;_free.IsChecked=_item.Access=="Free";_free.IsEnabled=!_item.IsBuiltin;
        panel.Children.Add(new TextBlock {Text="音色名称"});panel.Children.Add(_name);
        panel.Children.Add(new TextBlock {Text="说明"});panel.Children.Add(_description);panel.Children.Add(_free);
        if(!_item.IsBuiltin){AddFile(panel,"模型 .pth","model",["*.pth"]);AddFile(panel,"索引 .index","index",["*.index"]);}
        AddFile(panel,"封面 PNG / JPG","cover",["*.png","*.jpg","*.jpeg"]);
        panel.Children.Add(_coverPreview);AddFile(panel,"试听 WAV / MP3 / M4A","preview",["*.wav","*.mp3","*.m4a"]);
        var play=new Button {Content="试听所选样本"};play.Click+=(_,_)=>
        {try{if(_preview is not null){if(Path.GetExtension(_preview).Equals(".wav",StringComparison.OrdinalIgnoreCase))StoreMedia.Play(_preview);else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_preview){UseShellExecute=true});}}catch(Exception ex){_status.Text=ex.Message;}};
        panel.Children.Add(play);AddFile(panel,"可选：模型参数 JSON","parameters",["*.json"]);
        panel.Children.Add(new TextBlock {Text="上传完成仅保存为草稿，确认后发布。已发布版本不会被覆盖。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray});
        panel.Children.Add(_status);_upload.Click+=async (_,_)=>await Upload();panel.Children.Add(_upload);
        _publish.Click+=async (_,_)=>
        {
            try{if(_job is not null){await vm.Store.PublishAsync(_job.ModelId,_job.Revision,_life.Token);_status.Text="已发布此版本。";_publish.IsEnabled=false;}}
            catch(Exception ex){_status.Text=ex.Message;}
        };panel.Children.Add(_publish);
        _retry.Click+=async (_,_)=>{try{if(_job is not null){await vm.Store.RetryAsync(_job.Id,_life.Token);await Poll();}}catch(Exception ex){_status.Text=ex.Message;}};panel.Children.Add(_retry);
        var recover=new Button {Content="恢复最近上传任务"};recover.Click+=async (_,_)=>
        {try{_job=(await vm.Store.JobsAsync(_life.Token)).FirstOrDefault(j=>string.IsNullOrEmpty(_item.Id)||j.ModelId==_item.Id);if(_job is not null)await Poll();else _status.Text="暂无上传任务。";}catch(Exception ex){_status.Text=ex.Message;}};panel.Children.Add(recover);
        if(!string.IsNullOrEmpty(_item.Id))
        {
            var unpublish=new Button {Content="下架音色"};unpublish.Click+=async (_,_)=>{try{await vm.Store.UnpublishAsync(_item.Id,_life.Token);_status.Text="已下架，停止新增下载。";}catch(Exception ex){_status.Text=ex.Message;}};panel.Children.Add(unpublish);
            try{_cover=StoreMedia.CopyEmbedded(_item.Id,"png");_preview=StoreMedia.CopyEmbedded(_item.Id,"wav");_coverPreview.Source=new Bitmap(_cover);UpdateFileLabels();}catch { }
            var sourceName=_item.BuiltinFile;
            if(string.IsNullOrEmpty(sourceName))sourceName=_item.Id switch {"20000000000000000000000000000001"=>"mygf-dx.pth","20000000000000000000000000000002"=>"mygf-xnf.pth","20000000000000000000000000000003"=>"mygf-zyss2.pth",_=>""};
            if(!_item.IsBuiltin && !string.IsNullOrEmpty(sourceName))
            {
                var model=Path.Combine(vm.EngineRoot,"assets","weights",sourceName);var index=Path.Combine(vm.EngineRoot,"assets","indices",Path.ChangeExtension(sourceName,".index"));
                if(File.Exists(model))_model=model;if(File.Exists(index))_index=index;
                var parameters=Path.ChangeExtension(model,".rvcstudio.json");if(File.Exists(parameters))_parameters=parameters;
                UpdateFileLabels();
            }
        }
        Content=new ScrollViewer {Content=panel};Closed+=(_,_)=>{_closed=true;_life.Cancel();StoreMedia.Stop();};
    }
    private void AddFile(StackPanel panel,string title,string key,string[] patterns)
    {
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10};var button=new Button {Content=title};var label=new TextBlock {Text="未选择",MaxWidth=330,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};_labels[key]=label;
        button.Click+=async (_,_)=>
        {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title=title,AllowMultiple=false,FileTypeFilter=[new FilePickerFileType(title){Patterns=patterns}]});
            var path=files.FirstOrDefault()?.TryGetLocalPath();if(path is null)return;
            switch(key){case "model":_model=path;break;case "index":_index=path;break;case "cover":_cover=path;try{_coverPreview.Source=new Bitmap(path);}catch{_status.Text="无法读取封面。";}break;case "preview":_preview=path;break;case "parameters":_parameters=path;break;}
            UpdateFileLabels();
        };row.Children.Add(button);row.Children.Add(label);panel.Children.Add(row);
    }
    private void UpdateFileLabels()
    {foreach(var (key,path) in new Dictionary<string,string?>{{"model",_model},{"index",_index},{"cover",_cover},{"preview",_preview},{"parameters",_parameters}})if(_labels.TryGetValue(key,out var label))label.Text=path is null?"未选择":Path.GetFileName(path);}
    private async Task Upload()
    {
        _upload.IsEnabled=false;
        try
        {
            if(!_vm.Store.CanManage)throw new UnauthorizedAccessException("请使用有商城管理权限的账号登录。");
            if(string.IsNullOrWhiteSpace(_name.Text)||_cover is null||_preview is null||(!_item.IsBuiltin&&(_model is null||_index is null)))throw new InvalidDataException("请补齐名称、模型、索引、封面和试听。");
            _item.Name=_name.Text.Trim();_item.Description=_description.Text??"";_item.Access=_free.IsChecked==true?"Free":"Member";
            if(!_item.IsBuiltin)
            {
                _status.Text="正在验证模型与索引…";var inspection=await _vm.InspectUploadAsync(_model!,_index!,_life.Token);
                _item.Version=inspection.Version;_item.SampleRate=inspection.SampleRate;_item.SupportsPitch=inspection.SupportsPitch;
            }
            var recommended=_parameters is null?_item.Recommended:await File.ReadAllTextAsync(_parameters,_life.Token);
            _=BuiltinVoices.Tuning(recommended);
            _status.Text="正在上传草稿…";_job=await _vm.Store.UploadAsync(_item,_model,_index,_cover,_preview,recommended,_life.Token);
            await Poll();
        }
        catch(OperationCanceledException){if(!_closed)_status.Text="已取消；后台已接收的任务可在下次打开时恢复查看。";}
        catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
        finally{if(!_closed)_upload.IsEnabled=true;}
    }
    private async Task Poll()
    {
        while(_job is not null&&!_life.IsCancellationRequested)
        {
            _job=await _vm.Store.JobAsync(_job.Id,_life.Token);_status.Text=_job.Message;
            _publish.IsEnabled=_job.State=="Ready";_retry.IsVisible=_job.State=="Failed";
            if(_job.State is "Ready" or "Failed")return;
            await Task.Delay(2000,_life.Token);
        }
    }
}
