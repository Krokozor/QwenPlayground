using System.Windows.Controls;

namespace QwenPlayground.App.Views;

/// <summary>
/// Меню C#-трека чата — отдельный UserControl (паттерн ShelfMenu): встраивается в
/// Popup (см. ChatView: CSharpMenuButton), не в ContextMenu — Aero2-шаблон ContextMenu
/// рендерил артефакт, а свой Popup даёт полный контроль над видом.
/// </summary>
public partial class CSharpMenu : UserControl
{
    public CSharpMenu()
    {
        InitializeComponent();
    }
}
