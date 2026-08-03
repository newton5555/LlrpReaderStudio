using System.Windows.Media;
using FontAwesome.Sharp;

namespace LlrpReaderStudio.Models;

public class NavigationItem
{
    public string Title { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public IconChar Icon { get; set; } = IconChar.None;
    public Brush? IconBrush { get; set; }
    public object ViewModel { get; set; } = null!;
}
