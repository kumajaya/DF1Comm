using Avalonia.ReactiveUI;
using DF1ProgramTool.ViewModels;

namespace DF1ProgramTool.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
