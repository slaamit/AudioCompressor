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

        private string?    _currentFilePath;
        private WaveFormat? _originalFormat;
        private float[]?   _originalSamples;
        private long        _originalFileSizeBytes;   // actual file size on disk
        private float[]?   _processedSamples;
        private byte[]?    _loadedCompressedData;
        private CompressedFileHeader.AlgoId _loadedAlgo;
        private int  _loadedBits;
        private int  _loadedSampleCount;
        private int  _loadedOrder;
        private CancellationTokenSource? _cts;

        // ── UI properties ────────────────────────────────────────────────────

        private string _audioInfo = "No audio loaded.";
        public  string  AudioInfo { get => _audioInfo; set { _audioInfo = value; OnPropertyChanged(); } }

        private double _progressValue;
        public  double  ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }

        private string _statusMessage = "Ready.";
        public  string  StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        private string _selectedAlgorithm = "Nonlinear Quantization";
        public  string  SelectedAlgorithm { get => _selectedAlgorithm; set { _selectedAlgorithm = value; OnPropertyChanged(); } }

        private int _newSampleRate = 44100;
        public  int  NewSampleRate { get => _newSampleRate; set { _newSampleRate = value; OnPropertyChanged(); } }

        private int _quantizationLevels = 256;
        public  int  QuantizationLevels { get => _quantizationLevels; set { _quantizationLevels = value; OnPropertyChanged(); } }

        public ObservableCollection<string> Algorithms { get; } = new()
        {
            "Nonlinear Quantization",
            "DPCM",
            "Predictive Differential Coding",
            "Delta Modulation",
            "Adaptive Delta Modulation"
        };

        private PlotModel? _waveformModel;
        public  PlotModel?  WaveformModel { get => _waveformModel; set { _waveformModel = value; OnPropertyChanged(); } }

        private PlotModel _compressionPlotModel = null!;
        public  PlotModel  CompressionPlotModel { get => _compressionPlotModel; set { _compressionPlotModel = value; OnPropertyChanged(); } }

        private string _compressionRatioText = "N/A";
        public  string  CompressionRatioText { get => _compressionRatioText; set { _compressionRatioText = value; OnPropertyChanged(); } }

        private string _speedText = "N/A";
        public  string  SpeedText { get => _speedText; set { _speedText = value; OnPropertyChanged(); } }

        private string _reportText = "";
        public  string  ReportText { get => _reportText; set { _reportText = value; OnPropertyChanged(); } }

        // ── Commands ─────────────────────────────────────────────────────────

        public ICommand LoadCommand               { get; }
        public ICommand PlayCommand               { get; }
        public ICommand StopCommand               { get; }
        public ICommand CompressCommand           { get; }
        public ICommand CancelCompressCommand     { get; }
        public ICommand ResetCommand              { get; }
        public ICommand SaveCompressedCommand     { get; }
        public ICommand DecompressFromFileCommand { get; }
        public ICommand DecompressLoadedCommand   { get; }
        public ICommand PlayDecompressedCommand   { get; }

        public MainViewModel()
        {
            LoadCommand               = new RelayCommand(ExecuteLoad);
            PlayCommand               = new RelayCommand(ExecutePlay);
            StopCommand               = new RelayCommand(ExecuteStop);
            CompressCommand           = new RelayCommand(ExecuteCompress);
            CancelCompressCommand     = new RelayCommand(() => _cts?.Cancel());
            ResetCommand              = new RelayCommand(ExecuteReset);
            SaveCompressedCommand     = new RelayCommand(ExecuteSaveCompressed);
            DecompressFromFileCommand = new RelayCommand(ExecuteDecompressFromFile);
            DecompressLoadedCommand   = new RelayCommand(ExecuteDecompressLoaded);
            PlayDecompressedCommand   = new RelayCommand(ExecutePlayDecompressed);

            CompressionPlotModel = new PlotModel { Title = "Compression Progress" };
            CompressionPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Progress (%)" });
            CompressionPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left,   Title = "%" });
            CompressionPlotModel.Series.Add(new LineSeries { Title = "Progress", Color = OxyColors.Blue });

            WaveformModel = new PlotModel { Title = "Audio Waveform" };
            WaveformModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Sample" });
            WaveformModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left,   Title = "Amplitude" });
        }

        // ── Waveform ─────────────────────────────────────────────────────────

        private void BuildWaveform(float[] samples)
        {
            var series = new LineSeries { Color = OxyColors.Blue, StrokeThickness = 0.5 };
            int step   = Math.Max(1, samples.Length / 2000);
            for (int i = 0; i < samples.Length; i += step)
                series.Points.Add(new DataPoint(i, samples[i]));
            WaveformModel!.Series.Clear();
            WaveformModel.Series.Add(series);
            WaveformModel.InvalidatePlot(true);
        }

        // ── Load ─────────────────────────────────────────────────────────────

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
                string sizeStr = fileSize >= 1024 * 1024
                    ? $"{fileSize / (1024.0 * 1024.0):F2} MB"
                    : $"{fileSize / 1024.0:F2} KB";

                AudioInfo =
                    $"File: {Path.GetFileName(path)}\n" +
                    $"Size: {sizeStr}\n" +
                    $"Duration: {props.duration:mm\\:ss}\n" +
                    $"Sample Rate: {props.sampleRate} Hz\n" +
                    $"Channels: {props.channels}\n" +
                    $"Bit Rate: {props.bitRate / 1000} kbps\n" +
                    $"Codec: {props.codec}";

                var (format, samples, fileSizeBytes) = _audioService.LoadAudio(path);
                _originalFormat        = format;
                _originalSamples       = samples;
                _originalFileSizeBytes = fileSizeBytes;  // store real file size
                _processedSamples      = null;
                _loadedCompressedData  = null;

                BuildWaveform(samples);
                StatusMessage        = "Audio loaded successfully.";
                ReportText           = "";
                CompressionRatioText = "N/A";
                SpeedText            = "N/A";
                ProgressValue        = 0;
                (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
                CompressionPlotModel.InvalidatePlot(true);
            }
            catch (Exception ex) { StatusMessage = $"Error loading file: {ex.Message}"; }
        }

        // ── Play / Stop ───────────────────────────────────────────────────────

        private void ExecutePlay()
        {
            if (_currentFilePath == null) { StatusMessage = "No file loaded."; return; }
            _audioService.Play(_currentFilePath);
        }

        private void ExecuteStop() => _audioService.Stop();

        // ── Compress ─────────────────────────────────────────────────────────

        private async void ExecuteCompress()
        {
            if (_originalSamples == null || _originalFormat == null)
            { StatusMessage = "No audio loaded."; return; }

            // Cancel any previous run, create a fresh token.
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            StatusMessage        = "Compressing…";
            ProgressValue        = 0;
            CompressionRatioText = "0%";
            SpeedText            = "0 KB/s";
            (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
            CompressionPlotModel.InvalidatePlot(true);

            var  sw           = Stopwatch.StartNew();

            // Use the real file size on disk as the "original" size baseline.
            // This gives a meaningful ratio vs what the user actually had on disk.
            long originalSize = _originalFileSizeBytes;
            int  bits         = (int)Math.Log2(QuantizationLevels);
            int  sampleCount  = _originalSamples.Length;
            var  samples      = _originalSamples;
            var  algo         = SelectedAlgorithm;

            byte[]?    compressedData = null;
            float[]?   decompressed   = null;
            bool       wasCancelled   = false;
            Exception? bgException    = null;

            // IMPORTANT: No token passed to Task.Run.
            // Cancellation is handled entirely inside the lambda via try/catch.
            // Passing the token to Task.Run would cause .NET to cancel the Task
            // at the framework level, bypassing the catch block.
            await Task.Run(() =>
            {
                try
                {
                    void ReportProgress(int pct)
                    {
                        if (token.IsCancellationRequested) return;
                        double speed = originalSize / 1024.0 / (sw.Elapsed.TotalSeconds + 0.001);
                        App.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            ProgressValue        = pct;
                            CompressionRatioText = $"{pct}%";
                            SpeedText            = $"{speed:F0} KB/s";
                            (CompressionPlotModel.Series[0] as LineSeries)
                                ?.Points.Add(new DataPoint(pct, pct));
                            CompressionPlotModel.InvalidatePlot(true);
                        });
                    }

                    compressedData = Encode(algo, samples, bits, token);
                    token.ThrowIfCancellationRequested();

                    for (int p = 5; p <= 100; p += 5) ReportProgress(p);

                    decompressed = Decode(algo, compressedData, bits, sampleCount);
                    token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    wasCancelled   = true;
                    compressedData = null;
                    decompressed   = null;
                }
                catch (Exception ex)
                {
                    bgException    = ex;
                    compressedData = null;
                    decompressed   = null;
                }
            });  // ← no token here

            sw.Stop();

            if (wasCancelled)
            {
                StatusMessage        = "Compression cancelled.";
                ReportText           = "Compression cancelled by user.";
                ProgressValue        = 0;
                CompressionRatioText = "N/A";
                SpeedText            = "N/A";
                (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
                CompressionPlotModel.InvalidatePlot(true);
                return;
            }

            if (bgException != null)
            {
                StatusMessage = $"Compression error: {bgException.Message}";
                return;
            }

            // ── Success ───────────────────────────────────────────────────────
            _processedSamples = decompressed;

            long   compressedSize = compressedData!.Length;

            // Saving ratio: how much smaller is the compressed payload vs the
            // original file on disk.  A positive value means the file shrank.
            double savingRatio = (1.0 - (double)compressedSize / originalSize) * 100.0;

            ReportText =
                $"--- Compression Report ---\n" +
                $"Algorithm:       {SelectedAlgorithm}\n" +
                $"Original file:   {originalSize / 1024.0:F2} KB  (on disk)\n" +
                $"Compressed size: {compressedSize / 1024.0:F2} KB\n" +
                $"Saving ratio:    {savingRatio:F1}%\n" +
                $"Time taken:      {sw.Elapsed.TotalSeconds:F2} sec\n" +
                $"Speed:           {originalSize / 1024.0 / sw.Elapsed.TotalSeconds:F0} KB/s";

            StatusMessage        = $"Compressed with {SelectedAlgorithm}.";
            ProgressValue        = 100;
            CompressionRatioText = "100%";
            BuildWaveform(decompressed!);
        }

        // ── Encode / Decode (static — cannot accidentally touch UI thread) ────

        private static byte[] Encode(string algo, float[] samples, int bits, CancellationToken token) =>
            algo switch
            {
                "Nonlinear Quantization"         => MuLawCodec.Encode(samples, token),
                "DPCM"                           => DpcmCodec.Encode(samples, bits, token),
                "Predictive Differential Coding" => PredictiveCodec.Encode(samples, bits, 2, token),
                "Delta Modulation"               => DeltaModulationCodec.Encode(samples, 0.05f, token),
                "Adaptive Delta Modulation"      => AdaptiveDeltaModulationCodec.Encode(samples, 0.02f, 0.2f, 1.5f, token),
                _ => throw new InvalidOperationException($"Unknown algorithm: {algo}")
            };

        private static float[] Decode(string algo, byte[] data, int bits, int sampleCount) =>
            algo switch
            {
                "Nonlinear Quantization"         => MuLawCodec.Decode(data),
                "DPCM"                           => DpcmCodec.Decode(data, bits, sampleCount),
                "Predictive Differential Coding" => PredictiveCodec.Decode(data, bits, sampleCount, 2),
                "Delta Modulation"               => DeltaModulationCodec.Decode(data, sampleCount, 0.05f),
                "Adaptive Delta Modulation"      => AdaptiveDeltaModulationCodec.Decode(data, sampleCount, 0.02f, 0.2f, 1.5f),
                _ => throw new InvalidOperationException($"Unknown algorithm: {algo}")
            };

        // ── Reset ─────────────────────────────────────────────────────────────

        private void ExecuteReset()
        {
            _processedSamples     = null;
            _loadedCompressedData = null;
            StatusMessage         = "Reset.";
            ReportText            = "";
            CompressionRatioText  = "N/A";
            SpeedText             = "N/A";
            ProgressValue         = 0;
            (CompressionPlotModel.Series[0] as LineSeries)?.Points.Clear();
            CompressionPlotModel.InvalidatePlot(true);
            if (_originalSamples != null) BuildWaveform(_originalSamples);
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private void ExecuteSaveCompressed()
        {
            if (_originalSamples == null || _originalFormat == null)
            { StatusMessage = "Load an audio file first."; return; }

            var dlg = new SaveFileDialog
            {
                Filter      = "Compressed Binary|*.bin",
                FileName    = "output.bin",
                FilterIndex = 1
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                int bits = (int)Math.Log2(QuantizationLevels);

                    byte[] payload = Encode(
        SelectedAlgorithm,
        _originalSamples,
        bits,
        CancellationToken.None);

                    var algoId = AlgoIdFromName(SelectedAlgorithm);

                    byte[] header = CompressedFileHeader.Build(
                        algoId,
                        bits,
                        _originalSamples.Length,
                        _originalFormat.SampleRate,
                        _originalFormat.Channels,
                        order: 2);

                    using var fs = File.Create(dlg.FileName);
                    fs.Write(header);
                    fs.Write(payload);               

                StatusMessage = $"Saved: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
        }

        // ── Load .bin ─────────────────────────────────────────────────────────

        private void ExecuteDecompressFromFile()
        {
            var dlg = new OpenFileDialog { Filter = "Compressed Binary|*.bin" };
            if (dlg.ShowDialog() != true) return;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(dlg.FileName);

                if (!CompressedFileHeader.TryParse(fileBytes,
                        out _loadedAlgo, out _loadedBits, out _loadedSampleCount,
                        out int sampleRate, out int channels, out _loadedOrder))
                {
                    StatusMessage = "File has no valid header. Re-save it with this application.";
                    return;
                }

                _originalFormat = new WaveFormat(sampleRate, channels);

                _loadedCompressedData = new byte[fileBytes.Length - CompressedFileHeader.Size];
                Buffer.BlockCopy(fileBytes, CompressedFileHeader.Size,
                                 _loadedCompressedData, 0, _loadedCompressedData.Length);

                SelectedAlgorithm = AlgoNameFromId(_loadedAlgo);
                StatusMessage = $"Loaded {Path.GetFileName(dlg.FileName)} " +
                                $"({_loadedSampleCount:N0} samples, {AlgoNameFromId(_loadedAlgo)}). " +
                                "Click 'Decompress Loaded'.";
            }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        }

        // ── Decompress loaded .bin ────────────────────────────────────────────

        private void ExecuteDecompressLoaded()
        {
            if (_loadedCompressedData == null)
            { StatusMessage = "No compressed file loaded."; return; }

            try
            {
                float[] decompressed = Decode(
                    AlgoNameFromId(_loadedAlgo),
                    _loadedCompressedData,
                    _loadedBits,
                    _loadedSampleCount);

                _processedSamples = decompressed;
                BuildWaveform(decompressed);
                StatusMessage = "Decompressed. Click 'Play Decompressed' to listen.";
            }
            catch (Exception ex) { StatusMessage = $"Decompression failed: {ex.Message}"; }
        }

        // ── Play decompressed ─────────────────────────────────────────────────

        private void ExecutePlayDecompressed()
        {
            if (_processedSamples == null || _originalFormat == null)
            { StatusMessage = "No decompressed audio available."; return; }
            _audioService.PlayFromMemory(
                _processedSamples,
                _originalFormat.SampleRate,
                _originalFormat.Channels);
            StatusMessage = "Playing decompressed audio.";
        }

        // ── WAV writer ────────────────────────────────────────────────────────

        private static void SaveWav(string path, float[] samples, int sampleRate, int channels)
        {
            int dataBytes = samples.Length * 2;
            using var ms     = new MemoryStream(44 + dataBytes);
            using var writer = new BinaryWriter(ms);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);                    // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);   // byte rate
            writer.Write((short)(channels * 2));       // block align
            writer.Write((short)16);                   // bits per sample
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);

            foreach (float s in samples)
                writer.Write((short)Math.Clamp(
                    (int)(s * short.MaxValue), short.MinValue, short.MaxValue));

            File.WriteAllBytes(path, ms.ToArray());
        }

        // ── Algorithm ID mapping ──────────────────────────────────────────────

        private static CompressedFileHeader.AlgoId AlgoIdFromName(string name) => name switch
        {
            "Nonlinear Quantization"         => CompressedFileHeader.AlgoId.MuLaw,
            "DPCM"                           => CompressedFileHeader.AlgoId.Dpcm,
            "Predictive Differential Coding" => CompressedFileHeader.AlgoId.Predictive,
            "Delta Modulation"               => CompressedFileHeader.AlgoId.Delta,
            "Adaptive Delta Modulation"      => CompressedFileHeader.AlgoId.AdaptiveDelta,
            _ => throw new InvalidOperationException($"Unknown algorithm: {name}")
        };

        private static string AlgoNameFromId(CompressedFileHeader.AlgoId id) => id switch
        {
            CompressedFileHeader.AlgoId.MuLaw         => "Nonlinear Quantization",
            CompressedFileHeader.AlgoId.Dpcm           => "DPCM",
            CompressedFileHeader.AlgoId.Predictive     => "Predictive Differential Coding",
            CompressedFileHeader.AlgoId.Delta          => "Delta Modulation",
            CompressedFileHeader.AlgoId.AdaptiveDelta  => "Adaptive Delta Modulation",
            _ => throw new InvalidOperationException($"Unknown AlgoId: {id}")
        };

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? _) => true;
        public void Execute(object? _)    => _execute();
    }
}
