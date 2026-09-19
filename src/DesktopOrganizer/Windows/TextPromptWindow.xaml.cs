using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopOrganizer.Windows;

/// <summary>
/// 通用文本输入对话框（重命名等）。
/// 可选带一个复选框，用于「同时重命名磁盘文件夹」这类附加选项。
/// </summary>
public partial class TextPromptWindow : Window
{
    private bool _initialized;

    /// <summary>用户输入的文本（已去掉首尾空白）。</summary>
    public string Value => PromptBox.Text.Trim();

    /// <summary>附加复选框是否勾选。</summary>
    public bool OptionChecked => OptionCheck.IsChecked == true;

    public TextPromptWindow(string title, string label, string initialValue, string? optionText = null)
    {
        InitializeComponent();

        HeaderText.Text = title;
        LabelText.Text = label;
        PromptBox.Text = initialValue;
        PromptBox.SelectAll();

        if (!string.IsNullOrEmpty(optionText))
        {
            OptionCheck.Content = optionText;
            OptionCheck.Visibility = Visibility.Visible;
        }

        _initialized = true;

        Loaded += (_, _) =>
        {
            PromptBox.Focus();
            PromptBox.SelectAll();
            Validate();
        };
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (!_initialized)
        {
            return;
        }

        BtnConfirm.IsEnabled = Value.Length > 0;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignore
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && BtnConfirm.IsEnabled)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            return;
        }

        DialogResult = true;
    }
}
