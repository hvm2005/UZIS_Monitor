using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using UZIS_Monitor.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace UZIS_Monitor.ViewModels
{
    public partial class PacketViewModel : ObservableObject
    {
        [ObservableProperty] private string? _time; // TimeSpan лучше в строку для UI
        [ObservableProperty] private string? _polarity;
        [ObservableProperty] private ushort _evPhase2;
        [ObservableProperty] private ushort _evPhase4;
        [ObservableProperty] private double _sigmaKkm;
        [ObservableProperty] private short _accumulator;
        [ObservableProperty] private double _lineVoltage;
        [ObservableProperty] private bool _isCrcValid;
        [ObservableProperty] private bool _isEmpty;
        //[ObservableProperty] private ushort _crc16;
        //[ObservableProperty] private ushort _calcCrc;

        // Метод быстрого обновления БЕЗ пересоздания объекта
        public void Update(in PacketData data)
        {
            Time = data.Time.ToString(@"mm\:ss\.fff");
            Polarity = data.Polarity;
            EvPhase2 = data.EvPhase2;
            EvPhase4 = data.EvPhase4;
            SigmaKkm = data.SigmaKkm;
            Accumulator = data.Accumulator;
            LineVoltage = data.LineVoltage;
            IsCrcValid = data.IsCrcValid;
            IsEmpty = data.IsEmpty;
            //Crc16 = data.Crc16;
            //CalcCrc = data.CalcCrc;
        }
    }
}
