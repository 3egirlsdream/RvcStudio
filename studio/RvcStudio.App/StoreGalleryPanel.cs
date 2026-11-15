using Avalonia;
using Avalonia.Controls;

namespace RvcStudio.App;

// Expanded download and management controls increase row height without overlap.
public sealed class StoreGalleryPanel : Panel
{
    private const double ColumnGap=24,RowGap=30,MinimumCardWidth=196;
    private static int Columns(double width)=>Math.Clamp((int)Math.Floor((width+ColumnGap)/(MinimumCardWidth+ColumnGap)),1,4);
    protected override Size MeasureOverride(Size availableSize)
    {
        var width=double.IsFinite(availableSize.Width)?availableSize.Width:1100;
        var columns=Columns(width);var cardWidth=Math.Max(0,(width-(columns-1)*ColumnGap)/columns);
        double height=0,rowHeight=0;int position=0;
        foreach(var child in Children.Where(c=>c.IsVisible)) {
            if(child is StoreItemCard card)card.SetWidth(cardWidth);
            child.Measure(new Size(cardWidth,double.PositiveInfinity));rowHeight=Math.Max(rowHeight,child.DesiredSize.Height);
            if(++position%columns==0){height+=rowHeight+RowGap;rowHeight=0;}
        }
        if(position%columns!=0)height+=rowHeight;else if(position>0)height-=RowGap;
        return new Size(width,height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns=Columns(finalSize.Width);var width=Math.Max(0,(finalSize.Width-(columns-1)*ColumnGap)/columns);
        var children=Children.Where(c=>c.IsVisible).ToArray();double y=0;
        for(var start=0;start<children.Length;start+=columns) {
            var row=children.Skip(start).Take(columns).ToArray();var height=row.Max(c=>c.DesiredSize.Height);
            for(var column=0;column<row.Length;column++)row[column].Arrange(new Rect(column*(width+ColumnGap),y,width,height));
            y+=height+RowGap;
        }
        return finalSize;
    }
}
