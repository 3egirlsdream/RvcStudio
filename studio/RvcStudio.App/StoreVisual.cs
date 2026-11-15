using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace RvcStudio.App;

internal static class StoreVisual
{
    private static readonly Dictionary<string,Geometry> Icons=[];
    // Unmodified Phosphor assets, with their MIT license bundled in Assets.
    public static PathIcon Icon(string name,double size=16,string color="#AEB6B0")
    {
        if(!Icons.TryGetValue(name,out var geometry)) {
            using var stream=AssetLoader.Open(new Uri($"avares://RVC Studio/Assets/Store/Icons/{name}.svg"));
            var svg=XDocument.Load(stream);
            geometry=Geometry.Parse(string.Join(" ",svg.Descendants().Where(e=>e.Name.LocalName=="path").Select(e=>(string?)e.Attribute("d"))));
            Icons[name]=geometry;
        }
        return new PathIcon {Data=geometry,Width=size,Height=size,Foreground=Brush.Parse(color),VerticalAlignment=VerticalAlignment.Center};
    }
    public static StackPanel ListenContent(bool playing,bool loading=false)
    {
        var content=new StackPanel {Orientation=Orientation.Horizontal,Spacing=7,HorizontalAlignment=HorizontalAlignment.Center};
        content.Children.Add(Icon(loading?"arrow-clockwise":playing?"stop":"play",13,"#DDE3DE"));
        content.Children.Add(new TextBlock {Text=loading?"加载中":playing?"停止":"试听",FontSize=13,Foreground=Brush.Parse("#DDE3DE"),VerticalAlignment=VerticalAlignment.Center});
        return content;
    }
}
