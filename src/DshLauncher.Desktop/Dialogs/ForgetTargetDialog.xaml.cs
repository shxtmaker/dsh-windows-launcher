using System.Collections.ObjectModel;
using System.Windows;
using DshLauncher.Desktop.ViewModels;

namespace DshLauncher.Desktop.Dialogs;

public sealed class ForgetTargetDialogModel
{
    public required string TargetName { get; init; }
    public required string Endpoint { get; init; }
    public ObservableCollection<LauncherTarget> Successors { get; } = [];
}

public partial class ForgetTargetDialog : Window
{
    private readonly ForgetTargetDialogModel _model;

    public ForgetTargetDialog(ForgetTargetDialogModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        SuccessorPanel.Visibility = model.Successors.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        SuccessorBox.SelectedIndex = model.Successors.Count > 0 ? 0 : -1;
    }

    public Guid? SuccessorTargetId { get; private set; }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_model.Successors.Count > 1 && SuccessorBox.SelectedItem is not LauncherTarget)
        {
            SuccessorBox.Focus();
            return;
        }

        SuccessorTargetId = (SuccessorBox.SelectedItem as LauncherTarget)?.TargetId;
        DialogResult = true;
    }
}
