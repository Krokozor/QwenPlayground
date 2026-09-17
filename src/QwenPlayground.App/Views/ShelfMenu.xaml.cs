using System.Windows;
using System.Windows.Controls;

namespace QwenPlayground.App.Views;

/// <summary>
/// Меню полок (групп инструментов) — отдельный UserControl, чтобы его можно было
/// инспектировать и править независимо от ChatView. Встраивается в Popup (см.
/// ChatView: ShelfButton) — не в ContextMenu: дефолтный шаблон ContextMenu (Aero2)
/// рендерил артефакт (белый блок слева), а свой Popup даёт полный контроль над видом.
/// </summary>
public partial class ShelfMenu : UserControl
{
    public ShelfMenu()
    {
        InitializeComponent();
    }
}
