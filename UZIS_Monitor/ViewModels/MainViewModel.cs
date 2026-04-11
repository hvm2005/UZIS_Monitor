using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection.Metadata;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using UZIS_Monitor.Models;
using UZIS_Monitor.Services;
using UZIS_Monitor.Services.Interfaces;

namespace UZIS_Monitor.ViewModels;

public enum RecordingStates
{
    Idle,          // Ничего не происходит
    Waiting,       // Ждем первый пакет (IsWaitRecording)
    Recording      // Процесс идет (IsRecording)
}

public partial class MainViewModel : ObservableObject
{
    private readonly SerialPacketService _serialService;
    private readonly IEnumerable<IDataExporter> _exporters;
    private readonly IEnumerable<IDataImporter> _importers;

    // Внутреннее хранилище для высокоскоростной записи
    private readonly List<PacketData> _historyBuffer = new();
    private readonly ConcurrentQueue<PacketData> _uiQueue = new();
    private readonly object _lock = new();
    private readonly DispatcherTimer _uiRefreshTimer;

    [ObservableProperty] private PacketData _currentPacket;
    [ObservableProperty] private string _statusMessage = "Ожидание подключения...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private TimeSpan _recordTime;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    private RecordingStates _recordingState = RecordingStates.Idle;
    // Свойство для индикации наличия связи в UI (например, для зеленой лампочки)

    // Коллекция для DataGrid (обновляется только при необходимости)
    public ObservableCollection<PacketData> DisplayHistory { get; } = new();

    private double _voltageSum;
    private int _voltageCount;

    [ObservableProperty] private double _smoothVoltage;

    [ObservableProperty] private int _currentAcc;
    [ObservableProperty] private int _peakAcc;
    [ObservableProperty] private int _offCount;

    /// <summary>
    /// Обработчик события из фонового потока порта (100 Гц)
    /// </summary>
    private void HandleNewPacket(PacketData packet)
    {
        // 1. Обновляем текущие показатели (для текстовых блоков)
        CurrentPacket = packet;

        lock (_lock)
        {
            // 2. Накапливаем для усреднения в UI
            _voltageSum += packet.LineVoltage;
            _voltageCount++;

            CurrentAcc = packet.Accumulator;
            if (packet.Accumulator == -1)
            {
                PeakAcc = 0;
                OffCount++;
            }
            else
            {
                PeakAcc = Math.Max(PeakAcc, CurrentAcc);
            }

            // Если мы в режиме ожидания, проверяем условие старта
            if (RecordingState == RecordingStates.Waiting)
            {
                // Например: напряжение превысило 220В
                if (!packet.IsEmpty)
                {
                    App.Current.Dispatcher.Invoke(() => RecordingState = RecordingStates.Recording);
                    // Не выходим! Этот же пакет (первый подошедший) уже должен попасть в запись
                }
            }

            // 3. Если включена запись — сохраняем в буфер
            if (RecordingState == RecordingStates.Recording)
            {
                //packet.Number = _historyBuffer.Count + 1;
                //packet.Time = TimeSpan.FromMilliseconds(packet.Number * 10);
                // 1. Кладем в список для будущего сохранения в файл
                _historyBuffer.Add(packet);

                // 2. Кладем в очередь для отображения в таблице
                _uiQueue.Enqueue(packet);
            }
        }
    }

    /// <summary>
    /// Обновление визуальных элементов в UI-потоке
    /// </summary>
    private void RefreshUI()
    {
        var avgVoltage = 0.0d;
        var count = 0;

        lock (_lock)
        {
            avgVoltage = _voltageSum;
            count = _voltageCount;
            _voltageSum = 0;
            _voltageCount = 0;
        }

        if (count > 0) SmoothVoltage = avgVoltage / count;

        // Уведомляем систему, что свойства внутри структуры могли измениться
        OnPropertyChanged(nameof(CurrentPacket));

        RecordTime = TimeSpan.FromMilliseconds(_historyBuffer.Count * 10);

        // Проверяем саму очередь, а не флаг IsRecording.
        // Если в очереди что-то есть (даже если запись уже выключена), 
        // мы это заберем и покажем.
        if (!_uiQueue.IsEmpty)
        {
            SyncDisplayHistory();
        }
    }

    private void SyncDisplayHistory()
    {
        int limit = 100; // Не добавляем больше 100 строк за один раз
        while (limit > 0 && _uiQueue.TryDequeue(out var packet))
        {
            DisplayHistory.Add(packet);
            limit--;
        }
    }

    private async Task OpenFileAsync()
    {
        if (RecordingState == RecordingStates.Recording) return;

        // Используем уже готовый и правильно типизированный список импортеров
        var importersList = _importers.ToList();
        if (!importersList.Any()) return;

        // Собираем фильтр, приводя к интерфейсу с информацией о расширениях
        string combinedFilter = string.Join("|", importersList
            .Cast<IFileFormatInfo>()
            .Select(e => e.FileFilter));

        var openFileDialog = new Microsoft.Win32.OpenFileDialog { Filter = combinedFilter };

        if (openFileDialog.ShowDialog() == true)
        {
            var selectedImporter = importersList[openFileDialog.FilterIndex - 1];

            try
            {
                IsBusy = true;
                Mouse.OverrideCursor = Cursors.Wait;

                var loadedData = await selectedImporter.ImportAsync(openFileDialog.FileName);

                lock (_lock)
                {
                    _historyBuffer.Clear();
                    //_historyBuffer.TrimExcess();
                    _historyBuffer.AddRange(loadedData);
                    //foreach (var p in loadedData) _historyBuffer.Add(p);
                }

                DisplayHistory.Clear();
                foreach (var p in loadedData) DisplayHistory.Add(p);

                StatusMessage = $"Загружено: {loadedData.Count} строк";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async Task SaveBufferAsync()
    {
        if (_historyBuffer.Count == 0 || !_exporters.Any()) return;

        // Формируем общий фильтр для диалога из всех доступных экспортеров
        // Например: "Protobuf (*.pbin)|*.pbin|CSV (*.csv)|*.csv"
        string combinedFilter = string.Join("|", _exporters.Select(e => e.FileFilter));

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = combinedFilter,
            FileName = $"Data_{DateTime.Now:yyyyMMdd_HHmm}",
            Title = "Выберите формат и путь для сохранения"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            // Определяем, какой экспортер выбрал пользователь (по индексу фильтра)
            // FilterIndex в WinAPI начинается с 1
            var selectedExporter = _exporters.ElementAt(saveFileDialog.FilterIndex - 1);

            StatusMessage = $"Экспорт: {selectedExporter.DisplayName}...";

            try
            {
                // Вызываем выбранную стратегию сохранения
                await selectedExporter.ExportAsync(_historyBuffer, saveFileDialog.FileName);
                StatusMessage = "Данные успешно сохранены";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка экспорта: {ex.Message}";
            }
        }
    }

    private bool CanOpenFile() => RecordingState == RecordingStates.Idle;
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private async Task OpenFile()
    {
        await OpenFileAsync();
    }

    private bool CanSaveFile() => RecordingState == RecordingStates.Idle;
    [RelayCommand(CanExecute = nameof(CanSaveFile))]
    private async Task SaveFile()
    {
        await SaveBufferAsync();
    }

    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (RecordingState == RecordingStates.Idle)
        {
            // Старт записи
            ClearData();

            RecordingState = RecordingStates.Waiting;
        }
        else
        {
            // Стоп записи
            lock (_lock) RecordingState = RecordingStates.Idle;

            // Принудительно выгребаем остатки из очереди ПРЯМО СЕЙЧАС,
            // не дожидаясь следующего тика таймера.
            SyncDisplayHistory();

            // Теперь данные в таблице и в буфере 100% идентичны.
            await SaveFile();
        }
    }

    [RelayCommand]
    private void ResetReakAcc()
    {
        PeakAcc = 0;
    }

    [RelayCommand]
    private void ResetOffCount()
    {
        OffCount = 0;
    }

    private bool CanClear() => RecordingState == RecordingStates.Idle;
    [RelayCommand/*(CanExecute = nameof(CanClear))*/]
    private void ClearData()
    {
        lock (_lock)
        {
            _historyBuffer.Clear();
            //_historyBuffer.TrimExcess();
        }
        _uiQueue.Clear();
        DisplayHistory.Clear();

        ResetReakAcc();
        ResetOffCount();

        if (RecordingState == RecordingStates.Recording)
        {
            RecordingState = RecordingStates.Waiting;
        }
    }

    // DI-контейнер сам найдет этот конструктор и подставит сервис
    public MainViewModel(SerialPacketService serialService, IEnumerable<IDataExporter> exporters, IEnumerable<IDataImporter> importers)
    {
        _serialService = serialService;
        _exporters = exporters.ToList();
        _importers = importers.ToList();

        _serialService.OnPacketReceived += HandleNewPacket;
        _serialService.OnStatusChanged += (msg) => StatusMessage = msg;
        // Подписываемся на изменение состояния связи
        _serialService.OnConnectionStatusChanged += (connected) =>
        {
            // Обновляем свойство для лампочки в UI
            IsConnected = connected;

            // ГЛАВНОЕ: Если связь пропала (connected == false) И в данный момент шла запись
            if (!connected && (RecordingState == RecordingStates.Recording))
            {
                // Вызываем команду остановки записи
                // Это гарантирует, что данные из _historyBuffer попадут в DisplayHistory
                // Запускаем задачу в UI-потоке, но не ждем её (Fire and Forget)
                _ = App.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await ToggleRecordingCommand.ExecuteAsync(null);
                });

                StatusMessage = "Запись остановлена: потеря связи";
            }
        };

        // Позволяет изменять коллекцию из любого потока, WPF сам синхронизирует это с UI
        BindingOperations.EnableCollectionSynchronization(DisplayHistory, new object());

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiRefreshTimer.Tick += (s, e) => RefreshUI();
        _uiRefreshTimer.Start();
    }
}
