using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ProtoBuf.Meta;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection.Metadata;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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
    private readonly SerialService _serialService;
    private readonly IEnumerable<IDataExporter> _exporters;
    private readonly IEnumerable<IDataImporter> _importers;

    // Внутреннее хранилище для высокоскоростной записи
    private readonly List<PacketData> _historyBuffer = [];
    private readonly object _lock = new();
    private readonly DispatcherTimer _uiRefreshTimer;

    private PacketData _currentPacket;
    string _idleStatusMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string? _portName;
    [ObservableProperty] private TimeSpan _recordTime;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    private RecordingStates _recordingState = RecordingStates.Idle;
    // Свойство для индикации наличия связи в UI (например, для зеленой лампочки)

    // Коллекция для DataGrid (обновляется только при необходимости)
    [ObservableProperty] private object? _displayHistorySource;
    //private ObservableCollection<PacketData> DisplayHistory { get; } = [];

    // Постоянная коллекция "живых" строк
    private ObservableCollection<PacketViewModel> DisplayHistory { get; } =
        new(Enumerable.Range(0, 25).Select(_ => new PacketViewModel()));

    private double _voltageSum;
    private int _voltageCount;

    [ObservableProperty] private short _mth;
    [ObservableProperty] private double _smoothVoltage;

    [ObservableProperty] private int _currentAcc;
    [ObservableProperty] private int _peakAcc;
    [ObservableProperty] private int _offCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    private string _fileName = String.Empty;

    private async Task ListenToPacketsAsync()
    {
        await foreach (var packet in _serialService.PacketsReader.ReadAllAsync())
        {
            _currentPacket = packet;

            lock (_lock)
            {
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

                if (RecordingState == RecordingStates.Waiting)
                {
                    if (!packet.IsEmpty)
                    {
                        RecordingState = RecordingStates.Recording;
                    }
                }

                if (RecordingState == RecordingStates.Recording)
                {
                    _historyBuffer.Add(packet);
                }
            }
        }
    }

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

        Mth = _currentPacket.Mth;
        ClearDataCommand.NotifyCanExecuteChanged();

        if (RecordingState == RecordingStates.Recording)
        {
            if (DisplayHistorySource != DisplayHistory)
                DisplayHistorySource = DisplayHistory;

            var latest = CollectionsMarshal.AsSpan(_historyBuffer.TakeLast(DisplayHistory.Count).ToList());
            for (int i = 0; i < latest.Length; i++)
            {
                DisplayHistory[i].Update(latest[i]);
            }

            RecordTime = TimeSpan.FromMilliseconds(_historyBuffer.Count * 10);
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

        var openFileDialog = new OpenFileDialog { Filter = combinedFilter };

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
                    _historyBuffer.TrimExcess();
                }

                _historyBuffer.AddRange(loadedData);

                DisplayHistorySource = null;
                DisplayHistorySource = _historyBuffer;

                StatusMessage = $"Загружено: {loadedData.Count:N0} строк";
                FileName = System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName);
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

        // Вызывать ТОЛЬКО после тяжелых операций, типа загрузки файла
        GC.Collect(2, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
    }

    private async Task SaveBufferAsync()
    {
        if (_historyBuffer.Count == 0 || !_exporters.Any()) return;

        // Формируем общий фильтр для диалога из всех доступных экспортеров
        // Например: "Protobuf (*.pbin)|*.pbin|CSV (*.csv)|*.csv"
        string combinedFilter = string.Join("|", _exporters.Select(e => e.FileFilter));

        var saveFileDialog = new SaveFileDialog
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

                FileName = System.IO.Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
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

    private bool CanSaveFile() => (RecordingState == RecordingStates.Idle) && (_historyBuffer.Count > 0) && (FileName.Length == 0);
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

            ResetReakAcc();
            ResetOffCount();

            RecordTime = new TimeSpan(0);
            RecordingState = RecordingStates.Waiting;
        }
        else
        {
            // Стоп записи
            lock (_lock) RecordingState = RecordingStates.Idle;

            DisplayHistorySource = null;
            DisplayHistorySource = _historyBuffer;

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

    private bool CanClear() => _historyBuffer.Count > 0;
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearData()
    {
        lock (_lock)
        {
            _historyBuffer.Clear();
            _historyBuffer.TrimExcess();
        }

        DisplayHistorySource = null;

        FileName = String.Empty;
        SaveFileCommand.NotifyCanExecuteChanged();

        if (RecordingState == RecordingStates.Recording)
        {
            lock (_lock) RecordingState = RecordingStates.Waiting;
            RecordTime = new TimeSpan(0);
        }

        GC.Collect(2, GCCollectionMode.Optimized);
    }

    partial void OnStatusMessageChanged(string value)
    {
        if (value != "Готов" && !string.IsNullOrEmpty(value))
        {
            _ = ResetStatusAfterDelay();
        }
    }

    private int _statusToken = 0;

    private async Task ResetStatusAfterDelay()
    {
        var currentToken = ++_statusToken;
        await Task.Delay(5000);
        if (currentToken == _statusToken)
        {
            StatusMessage = _idleStatusMessage;
        }
    }

    // DI-контейнер сам найдет этот конструктор и подставит сервис
    public MainViewModel(SerialService serialService, IEnumerable<IDataExporter> exporters, IEnumerable<IDataImporter> importers)
    {
        _serialService = serialService;
        _exporters = exporters.ToList();
        _importers = importers.ToList();

        _ = ListenToPacketsAsync();

        //_serialService.OnStatusChanged += (msg) => StatusMessage = msg;
        // Подписываемся на изменение состояния связи
        _serialService.OnConnectionStatusChanged += (connected, portName) =>
        {
            // Обновляем свойство для лампочки в UI
            IsConnected = connected;
            PortName = portName;
            _idleStatusMessage = connected ? "Готов" : "Не подключено";
            StatusMessage = _idleStatusMessage;

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
        BindingOperations.EnableCollectionSynchronization(DisplayHistory, _lock);

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiRefreshTimer.Tick += (s, e) => RefreshUI();
        _uiRefreshTimer.Start();
    }
}
