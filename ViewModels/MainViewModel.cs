using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using AudioCompressor.Services;
using AudioCompressor.Helpers;
using NAudio.Wave;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.Axes;

namespace AudioCompressor.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly AudioService _audioService = new AudioService();
        private string? _currentFilePath;
        private WaveFormat? _originalFormat;
        private float[]? _originalSamples;
        private float[]? _processedSamples;
        private byte[]? _loadedCompressedData;
        private CancellationTokenSource? _cancellationTokenSource;

        // --- خصائص الواجهة الأساسية ---
        private string _audioInfo = "No audio loaded.";
        public string AudioInfo { get => _audioInfo; set { _audioInfo = value; OnPropertyChanged(); } }

        private double _progressValue = 0;
        public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }

        private string _statusMessage = "Ready.";
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        private string _selectedAlgorithm = "Nonlinear Quantization";
        public string SelectedAlgorithm { get => _selectedAlgorithm; set { _selectedAlgorithm = value; OnPropertyChanged(); } }

        private int _newSampleRate = 44100;
        public int NewSampleRate { get => _newSampleRate; set { _newSampleRate = value; OnPropertyChanged(); } }

        private int _quantizationLevels = 256;
        public int QuantizationLevels { get => _quantizationLevels; set { _quantizationLevels = value; OnPropertyChanged(); } }

        public ObservableCollection<string> Algorithms { get; } = new ObservableCollection<string>
        {
            "Nonlinear Quantization",
            "DPCM",
            "Predictive Differential Coding",
            "Delta Modulation",
            "Adaptive Delta Modulation"
        };

        // --- Waveform and Plots ---
        private PlotModel? _waveformModel;
        public PlotModel? WaveformModel
        {
            get => _waveformModel;
            set { _waveformModel = value; OnPropertyChanged(); }
        }

        private PlotModel _compressionPlotModel = null!;
        public PlotModel CompressionPlotModel
        {
            get => _compressionPlotModel;
            set { _compressionPlotModel = value; OnPropertyChanged(); }
        }

        private string _compressionRatioText = "N/A";
        public string CompressionRatioText { get => _compressionRatioText; set { _compressionRatioText = value; OnPropertyChanged(); } }

        private string _speedText = "N/A";
        public string SpeedText { get => _speedText; set { _speedText = value; OnPropertyChanged(); } }

        private string _reportText = "";
        public string ReportText { get => _reportText; set { _reportText = value; OnPropertyChanged(); } }

        // --- Commands ---
        public ICommand LoadCommand { get; }
        public ICommand PlayCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand CompressCommand { get; }
        public ICommand CancelCompressCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand SaveCompressedCommand { get; }
        public ICommand DecompressFromFileCommand { get; }
        public ICommand DecompressLoadedCommand { get; }
        public ICommand PlayDecompressedCommand { get; }

        public MainViewModel()
        {
            LoadCommand = new RelayCommand(ExecuteLoad);
            PlayCommand = new RelayCommand(ExecutePlay);
            StopCommand = new RelayCommand(ExecuteStop);
            CompressCommand = new RelayCommand(ExecuteCompress);
            CancelCompressCommand = new RelayCommand(ExecuteCancelCompress);
            ResetCommand = new RelayCommand(ExecuteReset);
            SaveCompressedCommand = new RelayCommand(ExecuteSaveCompressed);
            DecompressFromFileCommand = new RelayCommand(ExecuteDecompressFromFile);
            DecompressLoadedCommand = new RelayCommand(ExecuteDecompressLoaded);
            PlayDecompressedCommand = new RelayCommand(ExecutePlayDecompressed);

            CompressionPlotModel = new PlotModel { Title = "Compression Ratio (%)" };
            CompressionPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Progress (%)" });
            CompressionPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Ratio (%)" });
            CompressionPlotModel.Series.Add(new LineSeries { Title = "Ratio", Color = OxyColors.Blue });

            WaveformModel = new PlotModel { Title = "Audio Waveform" };
            WaveformModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Sample Index" });
            WaveformModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Amplitude" });
        }

        private void BuildWaveform(float[] samples)
        {
            var series = new LineSeries { Color = OxyColors.Blue, StrokeThickness = 0.5 };
            int step = Math.Max(1, samples.Length / 2000);
            for (int i = 0; i < samples.Length; i += step)
                series.Points.Add(new DataPoint(i, samples[i]));
            WaveformModel!.Series.Clear();
            WaveformModel.Series.Add(series);
            WaveformModel.InvalidatePlot(true);
        }

        private void ExecuteLoad()
        {
            var dlg = new OpenFileDialog { Filter = "Audio Files|*.wav;*.mp3;*.aiff;*.flac" };
            if (dlg.ShowDialog() == true) LoadAudio(dlg.FileName);
        }

        public void LoadAudioFromPath(string path) => LoadAudio(path);

        private void LoadAudio(string path)
        {
            try
            {
                _currentFilePath = path;
                var props = _audioService.GetProperties(path);
                long fileSize = new FileInfo(path).Length;
                string fileSizeStr = fileSize >= 1024 * 1024 ? $"{fileSize / (1024.0 * 1024.0):F2} MB" : $"{fileSize / 1024.0:F2} KB";

                AudioInfo = $"File: {Path.GetFileName(path)}\nSize: {fileSizeStr}\nDuration: {props.duration:mm\\:ss}\nSample Rate: {props.sampleRate} Hz\nChannels: {props.channels}\nBit Rate: {props.bitRate / 1000} kbps\nCodec: {props.codec}";

                var (format, samples) = _audioService.LoadAudio(path);
                _originalFormat = format;
                _originalSamples = samples;
                _processedSamples = null;
                _loadedCompressedData = null;
                BuildWaveform(samples);

                StatusMessage = "Audio loaded successfully.";
                ReportText = "";
                CompressionRatioText = "N/A";
                SpeedText = "N/A";
                ProgressValue = 0;
                (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
                CompressionPlotModel.InvalidatePlot(true);
            }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        }

        private void ExecutePlay() => _audioService.Play(_currentFilePath ?? throw new InvalidOperationException("No audio loaded."));
        private void ExecuteStop() => _audioService.Stop();

        private async void ExecuteCompress()
        {
            if (_originalSamples == null || _originalFormat == null) { StatusMessage = "No audio loaded."; return; }

            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            StatusMessage = "Compressing...";
            ProgressValue = 0;
            CompressionRatioText = "0%";
            SpeedText = "0 KB/s";
            (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
            CompressionPlotModel.InvalidatePlot(true);

            var stopwatch = Stopwatch.StartNew();
            byte[] compressedData = null;
            long originalSize = _originalSamples.Length * 4;
            long compressedSize = 0;
            int sampleCount = _originalSamples.Length;

            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i <= 100; i += 5)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        Thread.Sleep(50);
                        App.Current.Dispatcher.Invoke(() => ProgressValue = i);
                        double estRatio = i;
                        App.Current.Dispatcher.Invoke(() => CompressionRatioText = $"{estRatio:F0}%");
                        var series = CompressionPlotModel.Series[0] as LineSeries;
                        App.Current.Dispatcher.Invoke(() => series?.Points.Add(new DataPoint(i, estRatio)));
                        App.Current.Dispatcher.Invoke(() => CompressionPlotModel.InvalidatePlot(true));
                        double speed = (originalSize / 1024.0) / (stopwatch.Elapsed.TotalSeconds + 0.001);
                        App.Current.Dispatcher.Invoke(() => SpeedText = $"{speed:F0} KB/s");
                    }

                    switch (SelectedAlgorithm)
                    {
                        case "Nonlinear Quantization":
                            compressedData = Helpers.AudioCompressor.MuLawEncode(_originalSamples, token);
                            break;
                        case "DPCM":
                            compressedData = Helpers.AudioCompressor.DpcmEncode(_originalSamples, (int)Math.Log2(QuantizationLevels), token);
                            break;
                        case "Predictive Differential Coding":
                            compressedData = Helpers.AudioCompressor.PredictiveEncode(_originalSamples, (int)Math.Log2(QuantizationLevels), 2, token);
                            break;
                        case "Delta Modulation":
                            compressedData = Helpers.AudioCompressor.DeltaEncode(_originalSamples, 0.05f, token);
                            break;
                        case "Adaptive Delta Modulation":
                            compressedData = Helpers.AudioCompressor.AdaptiveDeltaEncode(_originalSamples, 0.02f, 0.2f, 1.5f, token);
                            break;
                    }
                    compressedSize = compressedData.Length;

                    float[] decompressed = null;
                    switch (SelectedAlgorithm)
                    {
                        case "Nonlinear Quantization":
                            decompressed = Helpers.AudioCompressor.MuLawDecode(compressedData);
                            break;
                        case "DPCM":
                            decompressed = Helpers.AudioCompressor.DpcmDecode(compressedData, (int)Math.Log2(QuantizationLevels));
                            break;
                        case "Predictive Differential Coding":
                            decompressed = Helpers.AudioCompressor.PredictiveDecode(compressedData, (int)Math.Log2(QuantizationLevels), 2);
                            break;
                        case "Delta Modulation":
                            decompressed = Helpers. AudioCompressor.DeltaDecode(compressedData, sampleCount, 0.05f);
                            break;
                        case "Adaptive Delta Modulation":
                            decompressed = Helpers.AudioCompressor.AdaptiveDeltaDecode(compressedData, sampleCount, 0.02f, 0.2f, 1.5f);
                            break;
                    }
                    _processedSamples = decompressed;
                    App.Current.Dispatcher.Invoke(() => { ProgressValue = 100; CompressionRatioText = "100%"; });
                }, token);

                stopwatch.Stop();
                double savingRatio = (1 - (double)compressedSize / originalSize) * 100;
                ReportText = $"--- Compression Report ---\nAlgorithm: {SelectedAlgorithm}\nOriginal size: {originalSize / 1024.0:F2} KB\nCompressed size: {compressedSize / 1024.0:F2} KB\nSaving ratio: {savingRatio:F1}%\nTime taken: {stopwatch.Elapsed.TotalSeconds:F2} sec\nSpeed: {(originalSize / 1024.0 / stopwatch.Elapsed.TotalSeconds):F0} KB/s";
                StatusMessage = $"Compressed using {SelectedAlgorithm}. Ready to play decompressed.";
            }
            catch (OperationCanceledException) { StatusMessage = "Compression cancelled."; ReportText = "Compression cancelled by user."; }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        }

        private void ExecuteCancelCompress() => _cancellationTokenSource?.Cancel();

        private void ExecuteReset()
        {
            _processedSamples = null;
            _loadedCompressedData = null;
            StatusMessage = "Reset to original. Compressed data cleared.";
            ReportText = "";
            CompressionRatioText = "N/A";
            SpeedText = "N/A";
            ProgressValue = 0;
            (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
            CompressionPlotModel.InvalidatePlot(true);
            if (_originalSamples != null) BuildWaveform(_originalSamples);
        }

        private void ExecuteSaveCompressed()
        {
            if (_processedSamples == null) { StatusMessage = "No decompressed data to save."; return; }
            var dlg = new SaveFileDialog { Filter = "WAV File|*.wav|Compressed Binary|*.bin", FileName = "output.wav", FilterIndex = 1 };
            if (dlg.ShowDialog() != true) return;
            try
            {
                if (dlg.FilterIndex == 2)
                {
                    byte[] compressed;
                    switch (SelectedAlgorithm)
                    {
                        case "Nonlinear Quantization": compressed = Helpers.AudioCompressor.MuLawEncode(_originalSamples!); break;
                        case "DPCM": compressed = Helpers.AudioCompressor.DpcmEncode(_originalSamples!, (int)Math.Log2(QuantizationLevels)); break;
                        case "Predictive Differential Coding": compressed = Helpers.AudioCompressor.PredictiveEncode(_originalSamples!, (int)Math.Log2(QuantizationLevels), 2); break;
                        case "Delta Modulation": compressed = Helpers.AudioCompressor.DeltaEncode(_originalSamples!); break;
                        default: compressed = Helpers.AudioCompressor.AdaptiveDeltaEncode(_originalSamples!); break;
                    }
                    File.WriteAllBytes(dlg.FileName, compressed);
                }
                else
                {
                    SaveWav(dlg.FileName, _processedSamples, _originalFormat!.SampleRate, _originalFormat.Channels);
                }
                StatusMessage = $"Saved to {dlg.FileName}";
            }
            catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
        }

        private void ExecuteDecompressFromFile()
        {
            var dlg = new OpenFileDialog { Filter = "Compressed Binary|*.bin" };
            if (dlg.ShowDialog() == true)
            {
                try { _loadedCompressedData = File.ReadAllBytes(dlg.FileName); StatusMessage = $"Loaded {Path.GetFileName(dlg.FileName)}. Click 'Decompress Loaded'."; }
                catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
            }
        }

        private void ExecuteDecompressLoaded()
        {
            if (_loadedCompressedData == null || _originalFormat == null) { StatusMessage = "No compressed data loaded or original format missing."; return; }
            try
            {
                float[] decompressed = null;
                switch (SelectedAlgorithm)
                {
                    case "Nonlinear Quantization":
                        decompressed = Helpers. AudioCompressor.MuLawDecode(_loadedCompressedData);
                        break;
                    case "DPCM":
                        decompressed = Helpers.AudioCompressor.DpcmDecode(_loadedCompressedData, (int)Math.Log2(QuantizationLevels));
                        break;
                    case "Predictive Differential Coding":
                        decompressed = Helpers.AudioCompressor.PredictiveDecode(_loadedCompressedData, (int)Math.Log2(QuantizationLevels), 2);
                        break;
                    case "Delta Modulation":
                        decompressed = Helpers. AudioCompressor.DeltaDecode(_loadedCompressedData, _loadedCompressedData.Length * 8, 0.05f);
                        break;
                    case "Adaptive Delta Modulation":
                        decompressed = Helpers. AudioCompressor.AdaptiveDeltaDecode(_loadedCompressedData, _loadedCompressedData.Length * 8, 0.02f, 0.2f, 1.5f);
                        decompressed = Helpers. AudioCompressor.AdaptiveDeltaDecode(_loadedCompressedData, _loadedCompressedData.Length * 8, 0.02f, 0.2f, 1.5f);
                        break;
                }
                _processedSamples = decompressed;
                BuildWaveform(decompressed);
                StatusMessage = "Decompressed successfully. Use 'Play Decompressed' to listen.";
            }
            catch (Exception ex) { StatusMessage = $"Decompression failed: {ex.Message}"; }
        }

        private void ExecutePlayDecompressed()
        {
            if (_processedSamples == null || _originalFormat == null)
            { StatusMessage = "No decompressed audio. Please compress or decompress a file first."; return; }
            _audioService.PlayFromMemory(_processedSamples, _originalFormat.SampleRate, _originalFormat.Channels);
            StatusMessage = "Playing decompressed audio directly from memory.";
        }

        private void SaveWav(string path, float[] samples, int sampleRate, int channels)
        {
            short[] shortSamples = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++) shortSamples[i] = (short)(samples[i] * short.MaxValue);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length * 2);
            foreach (var s in shortSamples) writer.Write(s);
            File.WriteAllBytes(path, ms.ToArray());
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}