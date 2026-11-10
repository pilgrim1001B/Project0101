using System.Drawing;
using System.Text;
using FddCompiler;

namespace FddGui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CompilerForm());
    }
}

internal sealed class CompilerForm : Form
{
    private readonly RichTextBox _source = new();
    private readonly TextBox _output = new();
    private readonly Button _runButton = new();

    public CompilerForm()
    {
        Text = "FDD — редактор и запуск программ";
        MinimumSize = new Size(720, 520);
        Size = new Size(960, 700);
        StartPosition = FormStartPosition.CenterScreen;

        _source.Dock = DockStyle.Fill;
        _source.Font = new Font("Consolas", 11);
        _source.AcceptsTab = true;
        _source.WordWrap = false;
        _source.Text = """
            let total = 0;
            let i = 1;
            while (i <= 5) {
                total = total + i;
                i = i + 1;
            }
            print total;
            """;

        _output.Dock = DockStyle.Fill;
        _output.Font = new Font("Consolas", 10);
        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;

        _runButton.Text = "▶ Запустить";
        _runButton.AutoSize = true;
        _runButton.Click += async (_, _) => await RunProgramAsync();

        var openButton = new Button { Text = "Открыть файл", AutoSize = true };
        openButton.Click += (_, _) => OpenFile();

        var saveButton = new Button { Text = "Сохранить", AutoSize = true };
        saveButton.Click += (_, _) => SaveFile();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            WrapContents = false
        };
        toolbar.Controls.AddRange(new Control[] { _runButton, openButton, saveButton });

        var editorGroup = new GroupBox
        {
            Text = "Программа",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        editorGroup.Controls.Add(_source);

        var outputGroup = new GroupBox
        {
            Text = "Результат",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        outputGroup.Controls.Add(_output);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(editorGroup, 0, 1);
        layout.Controls.Add(outputGroup, 0, 2);
        Controls.Add(layout);
    }

    private async Task RunProgramAsync()
    {
        var source = _source.Text;
        _runButton.Enabled = false;
        _source.Enabled = false;
        _output.Clear();
        try
        {
            var (result, error) = await Task.Run(() =>
            {
                var capturedOutput = new StringBuilder();
                using var writer = new StringWriter(capturedOutput);
                var originalOutput = Console.Out;
                try
                {
                    Console.SetOut(writer);
                    FddEngine.Execute(source);
                    return (capturedOutput.ToString(), (string?)null);
                }
                catch (CompileException exception)
                {
                    return (capturedOutput.ToString(), exception.Message);
                }
                catch (ArithmeticException exception)
                {
                    return (capturedOutput.ToString(), exception.Message);
                }
                finally
                {
                    Console.SetOut(originalOutput);
                }
            });

            _output.ForeColor = error is null ? SystemColors.WindowText : Color.DarkRed;
            _output.Text = error ?? (string.IsNullOrEmpty(result) ? "Программа выполнена." : result);
        }
        finally
        {
            _source.Enabled = true;
            _runButton.Enabled = true;
        }
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Файлы FDD (*.fdd)|*.fdd|Все файлы (*.*)|*.*",
            Title = "Открыть программу"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _source.Text = File.ReadAllText(dialog.FileName);
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось открыть файл",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(this, exception.Message, "Нет доступа к файлу",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Файлы FDD (*.fdd)|*.fdd|Все файлы (*.*)|*.*",
            DefaultExt = "fdd",
            AddExtension = true,
            Title = "Сохранить программу"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, _source.Text);
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось сохранить файл",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(this, exception.Message, "Нет доступа к файлу",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
