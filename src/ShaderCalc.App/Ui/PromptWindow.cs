using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShaderCalc.App.Ui;

/// <summary>A small modal text prompt (rename a worksheet).</summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    private PromptWindow(string title, string label, string initialText)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _input = new TextBox { Text = initialText, MinWidth = 320, Margin = new Thickness(0, 6, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_input, "PromptInput");
        Button ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PromptOk");
        Button cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => DialogResult = true;
        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(_input);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public static string? Ask(Window owner, string title, string label, string initialText)
    {
        PromptWindow prompt = new PromptWindow(title, label, initialText) { Owner = owner };
        return prompt.ShowDialog() == true ? prompt._input.Text : null;
    }
}
