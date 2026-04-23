using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using UZIS_Monitor.Models;
using Wpf.Ui.Controls;
using DataGrid = Wpf.Ui.Controls.DataGrid;

namespace UZIS_Monitor
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is PacketData dataItem)
            {
                // Ждем, когда шаблон применится к строке
                e.Row.ApplyTemplate();

                // Ищем тот самый Border по имени из шаблона wpf-ui
                var border = e.Row.Template.FindName("DGR_Border", e.Row) as Border;

                if (border != null)
                {
                    if (!dataItem.IsCrcValid)
                    {
                        //border.Background = Brushes.IndianRed;
                        //border.BorderBrush = Brushes.IndianRed;
                    }
                    else
                    {
                        // Обязательно сбрасываем фон для переиспользуемых строк
                        //border.Background = Brushes.Transparent;
                        //border.BorderBrush = Brushes.Transparent;
                    }
                }
            }
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is PacketData data)    // !!! Потом убрать отсюда модель !!!
            {
                // Если деталей нет — выходим сразу, не меняя Visibility
                if (data.EvPhase2 == 0)
                {
                    row.DetailsVisibility = Visibility.Collapsed;
                    row.IsSelected = true;
                    row.Focus();
                    e.Handled = true;
                    return;
                }

                // Проверяем, что клик пришелся на саму строку, а не на вложенный DataGrid
                // или другой интерактивный элемент
                if (e.OriginalSource is Visual visual && !IsChildOfDetails(visual, row))
                {
                    if (row.IsSelected)
                    {
                        // Переключаем видимость (Toggle)
                        row.DetailsVisibility = row.DetailsVisibility == Visibility.Visible
                            ? Visibility.Collapsed
                            : Visibility.Visible;

                        // Важно: останавливаем событие, чтобы стандартный механизм DataGrid 
                        // не перехватил клик и не сбросил Visibility обратно
                        //e.Handled = true;
                    }
                    else
                    {
                        // Если строка новая — просто показываем детали
                        //row.DetailsVisibility = Visibility.Visible;
                        //row.IsSelected = true;
                        //row.Focus();
                    }
                }
            }
        }

        // Вспомогательный метод, чтобы клики ВНУТРИ вложенного грида не закрывали детали
        private bool IsChildOfDetails(Visual element, DataGridRow row)
        {
            var detailsPresenter = FindVisualChild<DataGridDetailsPresenter>(row);
            return detailsPresenter != null && element.IsDescendantOf(detailsPresenter);
        }

        // Стандартный поиск дочернего элемента в Visual Tree
        private T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}