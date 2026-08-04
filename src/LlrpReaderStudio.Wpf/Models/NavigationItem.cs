namespace LlrpReaderStudio.Models;

public class NavigationItem
{
    public string Title { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
    public object ViewModel { get; set; } = null!;
}
