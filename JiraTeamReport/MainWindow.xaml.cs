using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace JiraTeamReport;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ReportRow> rows = new();
    private readonly ObservableCollection<string> apiLogs = new();
    private readonly List<DataGridColumn> employeeColumns = new();
    private DataGridColumn? dateColumn;
    private JiraTempoClient? client;
    private List<Team> teams = new();

    public MainWindow()
    {
        InitializeComponent(); ReportGrid.ItemsSource = rows; LogsList.ItemsSource = apiLogs;
        dateColumn = ReportGrid.Columns[0];
        var saved = ConnectionSettingsStore.Load();
        if (saved is not null)
        {
            UrlBox.Text = saved.Url;
            LoginBox.Text = saved.Login;
            PasswordBox.Password = saved.Password;
            RememberCheck.IsChecked = true;
        }
        FromDate.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); ToDate.SelectedDate = DateTime.Today;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            client?.Dispose(); client = null; teams.Clear(); TeamBox.ItemsSource = null; TeamBox.SelectedItem = null; TeamBox.IsEnabled = false; LoadButton.IsEnabled = false; ClearReport();
            PersistConnectionSettings();
            Busy(true, "Проверяю подключение и загружаю команды Tempo…");
            client = new JiraTempoClient(UrlBox.Text, LoginBox.Text, PasswordBox.Password, AddApiLog);
            teams = await client.GetTeamsAsync(); TeamBox.ItemsSource = teams; TeamBox.IsEnabled = teams.Count > 0;
            if (teams.Count > 0) TeamBox.SelectedIndex = 0;
            LoadButton.IsEnabled = teams.Count > 0;
            StatusText.Text = teams.Count == 0 ? "Команды не найдены. Нужен Tempo Timesheets и доступ к API." : $"Загружено команд: {teams.Count}";
        }
        catch (Exception ex) { Error(ex); } finally { Busy(false); }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || TeamBox.SelectedItem is not Team team || FromDate.SelectedDate is not DateTime from || ToDate.SelectedDate is not DateTime to) return;
        if (from.Date > to.Date) { MessageBox.Show("Дата начала должна быть не позже даты окончания.", "Период"); return; }
        try
        {
            Busy(true, "Читаю worklogs и формирую отчет…");
            var report = await client.BuildReportAsync(team, from.Date, to.Date); Render(report, from.Date, to.Date);
            StatusText.Text = $"Сотрудников: {report.Employees.Count}; дней: {(to.Date-from.Date).Days+1}";
        }
        catch (Exception ex) { Error(ex); } finally { Busy(false); }
    }

    private void DemoButton_Click(object sender, RoutedEventArgs e)
    {
        var from = FromDate.SelectedDate?.Date ?? DateTime.Today.AddDays(-6);
        var to = ToDate.SelectedDate?.Date ?? DateTime.Today;
        if (from > to) { MessageBox.Show("Дата начала должна быть не позже даты окончания.", "Период"); return; }

        var baseUrl = Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var jiraUri)
            ? jiraUri.GetLeftPart(UriPartial.Authority) + jiraUri.AbsolutePath.TrimEnd('/')
            : "https://jira.example.com";
        var employees = new[]
        {
            new Employee("demo-1", "Иван Петров"),
            new Employee("demo-2", "Мария Смирнова"),
            new Employee("demo-3", "Алексей Кузнецов")
        };
        var tasks = new[]
        {
            (Task: "Подготовка требований и согласование сценариев", TaskKey: "DEMO-101", Epic: "Развитие личного кабинета", EpicKey: "DEMO-10"),
            (Task: "Реализация формы отчетности", TaskKey: "DEMO-205", Epic: "Аналитическая отчетность", EpicKey: "DEMO-20"),
            (Task: "Проверка интеграции с Jira", TaskKey: "DEMO-306", Epic: "Интеграция и автоматизация", EpicKey: "DEMO-30")
        };
        var data = new ReportData();
        data.Employees.AddRange(employees);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            for (var i = 0; i < employees.Length; i++)
            {
                if ((day.Day + i) % 4 == 0) continue;
                var item = tasks[(day.Day + i) % tasks.Length];
                data.Cells[(employees[i].Id, day)] = new ReportCell(
                    item.Task, $"{baseUrl}/browse/{item.TaskKey}",
                    item.Epic, $"{baseUrl}/browse/{item.EpicKey}");
            }
        }
        Render(data, from, to);
        StatusText.Text = $"Демо-данные: {employees.Length} сотрудника; выберите ячейку и проверьте Ctrl+C и ссылки.";
    }
    private void Render(ReportData data, DateTime from, DateTime to)
    {
        ClearReport();
        for (var date=from; date<=to; date=date.AddDays(1)) rows.Add(new ReportRow(date));
        var columnIndex = 0;
        foreach (var employee in data.Employees.OrderBy(e=>e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var key = columnIndex++;
            foreach (var row in rows)
            {
                while (row.Values.Count <= key) row.Values.Add(ReportCell.Empty);
                row.Values[key] = data.Cells.TryGetValue((employee.Id,row.Date), out var cell) ? cell : ReportCell.Empty;
            }
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            var taskBlock = LinkBlock($"Values[{key}].TaskText", $"Values[{key}].TaskUrl", "#1769AA", true); var epicBlock = LinkBlock($"Values[{key}].EpicText", $"Values[{key}].EpicUrl", "#68778A", false);
            panel.AppendChild(taskBlock); panel.AppendChild(epicBlock);
            var template = new DataTemplate { VisualTree = panel };
            var col = new DataGridTemplateColumn { Header=employee.Name, Width=new DataGridLength(245), CellTemplate=template };
            employeeColumns.Add(col); ReportGrid.Columns.Add(col);
        }
    }

    private string GetSelectedCellText()
    {
        var current = ReportGrid.CurrentCell;
        var hasSelected = ReportGrid.SelectedCells.Count > 0;
        var selected = hasSelected ? ReportGrid.SelectedCells[ReportGrid.SelectedCells.Count - 1] : default;
        var item = current.Item is ReportRow ? current.Item : hasSelected ? selected.Item : null;
        var column = current.Item is ReportRow && current.Column is not null ? current.Column : hasSelected ? selected.Column : null;
        if (item is not ReportRow row || column is null) return "";
        if (column == dateColumn) return row.DateLabel;
        var valueIndex = employeeColumns.IndexOf(column);
        if (valueIndex < 0 || valueIndex >= row.Values.Count) return "";
        var cell = row.Values[valueIndex];
        return string.Join(Environment.NewLine, new[] { cell.TaskText, cell.EpicText }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private void UpdateCellPreview()
    {
        if (CellPreviewBox is not null) CellPreviewBox.Text = GetSelectedCellText();
    }

    private void ReportGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e) => UpdateCellPreview();
    private void ReportGrid_CurrentCellChanged(object? sender, EventArgs e) => UpdateCellPreview();

    private void ReportGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        CopySelectedCell(e);
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (CellPreviewBox.IsKeyboardFocusWithin) return;
        CopySelectedCell(e);
    }

    private void CopySelectedCell(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.C ||
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0 ||
            !ReportGrid.IsVisible) return;
        var text = GetSelectedCellText();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            e.Handled = true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = "Буфер обмена занят. Попробуйте скопировать еще раз.";
        }
    }    private void PersistConnectionSettings()
    {
        try
        {
            if (RememberCheck.IsChecked == true)
                ConnectionSettingsStore.Save(UrlBox.Text, LoginBox.Text, PasswordBox.Password);
            else
                ConnectionSettingsStore.Clear();
        }
        catch (Exception ex)
        {
            AddApiLog($"Не удалось сохранить данные подключения в Windows: {ex.Message}");
            MessageBox.Show("Не удалось сохранить данные подключения. Приложение продолжит работу, но введенные данные не сохранятся.", "Сохранение данных", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RememberCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        try { ConnectionSettingsStore.Clear(); }
        catch (Exception ex) { AddApiLog($"Не удалось удалить сохраненные данные подключения: {ex.Message}"); }
    }
    private void ClearLogsButton_Click(object sender, RoutedEventArgs e) => apiLogs.Clear();

    private void AddApiLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddApiLog(message));
            return;
        }
        const int maxLogEntries = 200;
        const int maxLogEntryLength = 16000;
        if (message.Length > maxLogEntryLength)
            message = message[..maxLogEntryLength] + Environment.NewLine + "… [запись сокращена в интерфейсе]";
        apiLogs.Add(message);
        while (apiLogs.Count > maxLogEntries) apiLogs.RemoveAt(0);
    }

    private void ClearReport()
    {
        rows.Clear();
        foreach (var column in employeeColumns) ReportGrid.Columns.Remove(column);
        employeeColumns.Clear();
    }
    private static FrameworkElementFactory Link(string textPath,string urlPath,string color,bool bold)
    {
        var link=new FrameworkElementFactory(typeof(Hyperlink)); link.SetBinding(Hyperlink.NavigateUriProperty,new System.Windows.Data.Binding(urlPath));
        link.SetValue(Hyperlink.ForegroundProperty,(Brush)new BrushConverter().ConvertFromString(color)!); link.SetValue(Hyperlink.TextDecorationsProperty,TextDecorations.Underline);
        link.AddHandler(Hyperlink.RequestNavigateEvent,new RequestNavigateEventHandler((s,e)=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});}catch{}}));
        var run=new FrameworkElementFactory(typeof(Run)); run.SetBinding(Run.TextProperty,new System.Windows.Data.Binding(textPath)); if(bold)run.SetValue(Run.FontWeightProperty,FontWeights.SemiBold); link.AppendChild(run); return link;
    }
    private static FrameworkElementFactory LinkBlock(string textPath,string urlPath,string color,bool bold)
    {
        var block=new FrameworkElementFactory(typeof(TextBlock)); block.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); block.SetValue(TextBlock.MarginProperty,new Thickness(0,1,0,1)); block.AppendChild(Link(textPath,urlPath,color,bold)); return block;
    }
    protected override void OnClosed(EventArgs e) { client?.Dispose(); base.OnClosed(e); }
    private void Busy(bool value,string? status=null){ConnectButton.IsEnabled=!value;LoadButton.IsEnabled=!value&&teams.Count>0;Cursor=value?System.Windows.Input.Cursors.Wait:null;if(status!=null)StatusText.Text=status;}
    private void Error(Exception ex){StatusText.Text="Ошибка подключения или отчета.";MessageBox.Show(ex.Message,"Jira / Tempo",MessageBoxButton.OK,MessageBoxImage.Error);}

    public sealed class ReportRow
    {
        public DateTime Date {get;} public string DateLabel=>Date.ToString("dd.MM.yy",CultureInfo.InvariantCulture); public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        public List<ReportCell> Values {get;}=new(); public ReportRow(DateTime date)=>Date=date;
    }
}











