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
        private WaveFormat? _processedFormat;
        private byte[]?    _compressedPayload;
        private CompressionSettings? _compressedSettings;
        private string _compressedFileExtension = ".acmp";
        private string _compressedFileBaseName = "compressed_audio";
        private CancellationTokenSource? _cts;
        private bool _suppressSettingsInvalidation;

        private const float DeltaStepSize = 0.05f;
        private const float AdaptiveMinStep = 0.02f;
        private const float AdaptiveMaxStep = 0.2f;
        private const float AdaptiveAlpha = 1.5f;

        private sealed record CompressionSettings(
            string Algorithm,
            CompressedFileHeader.AlgoId AlgoId,
            int Bits,
            int SampleCount,
            int SampleRate,
            int Channels,
            int Order);

        // ── UI properties ────────────────────────────────────────────────────

        private string _audioInfo = "No audio loaded.";
        public  string  AudioInfo { get => _audioInfo; set { _audioInfo = value; OnPropertyChanged(); } }

        private double _progressValue;
        public  double  ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }

        private string _statusMessage = "Ready.";
        public  string  StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        private string _selectedAlgorithm = "Adaptive Delta Modulation";
        public  string  SelectedAlgorithm
        {
            get => _selectedAlgorithm;
            set
            {
                _selectedAlgorithm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SettingsHelpText));
                InvalidateCompressedResult();
            }
        }

        private int _quantizationBits = 2;
        public  int  QuantizationBits
        {
            get => _quantizationBits;
            set { _quantizationBits = value; OnPropertyChanged(); InvalidateCompressedResult(); }
        }

        private int _predictorOrder = 2;
        public  int  PredictorOrder
        {
            get => _predictorOrder;
            set { _predictorOrder = value; OnPropertyChanged(); InvalidateCompressedResult(); }
        }

        private string _selectedSampleRateOption = "8000";
        public string SelectedSampleRateOption
        {
            get => _selectedSampleRateOption;
            set { _selectedSampleRateOption = value; OnPropertyChanged(); InvalidateCompressedResult(); }
        }

        public string SettingsHelpText => SelectedAlgorithm switch
        {
            "Nonlinear Quantization" => "Uses fixed 8-bit mu-law quantization. Usually larger than MP3 unless sample rate is reduced.",
            "DPCM" => "Uses Bits per sample. Use 1-2 bits and 8000 Hz for smaller files. Valid range: 1 to 8.",
            "Predictive Differential Coding" => "Uses Bits per sample and Predictor order. Use 1-2 bits and 8000 Hz for smaller files.",
            "Delta Modulation" => "Uses fixed 1-bit delta modulation. Small output, lower quality.",
            "Adaptive Delta Modulation" => "Uses fixed 1-bit adaptive delta modulation. Best default for small files.",
            _ => ""
        };

        public ObservableCollection<string> Algorithms { get; } = new()
        {
            "Nonlinear Quantization",
            "DPCM",
            "Predictive Differential Coding",
            "Delta Modulation",
            "Adaptive Delta Modulation"
        };

        public ObservableCollection<string> SampleRateOptions { get; } = new()
        {
            "Original",
            "44100",
            "22050",
            "16000",
            "8000"
        };

        private PlotModel? _waveformModel;
        public  PlotModel?  WaveformModel { get => _waveformModel; set { _waveformModel = value; OnPropertyChanged(); } }

        private PlotModel _compressionRatioPlotModel = null!;
        public  PlotModel  CompressionRatioPlotModel { get => _compressionRatioPlotModel; set { _compressionRatioPlotModel = value; OnPropertyChanged(); } }

        private PlotModel _compressionSpeedPlotModel = null!;
        public  PlotModel  CompressionSpeedPlotModel { get => _compressionSpeedPlotModel; set { _compressionSpeedPlotModel = value; OnPropertyChanged(); } }

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

            CompressionRatioPlotModel = new PlotModel { Title = "Compression Ratio" };
            CompressionRatioPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Progress (%)" });
            CompressionRatioPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Saving vs Original File (%)" });
            CompressionRatioPlotModel.Series.Add(new LineSeries { Title = "Saving vs Original File", Color = OxyColors.Green });

            CompressionSpeedPlotModel = new PlotModel { Title = "Compression Speed" };
            CompressionSpeedPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Progress (%)" });
            CompressionSpeedPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Speed (KB/s)" });
            CompressionSpeedPlotModel.Series.Add(new LineSeries { Title = "Processing Speed", Color = OxyColors.Orange });

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

        private void InvalidateCompressedResult()
        {
            if (_suppressSettingsInvalidation ||
                _compressedPayload == null ||
                _originalSamples == null)
                return;

            _compressedPayload = null;
            _compressedSettings = null;
            _processedSamples = null;
            _processedFormat = null;
            CompressionRatioText = "N/A";
            ReportText = "Compression settings changed. Compress again before saving.";
            StatusMessage = "Settings changed. Compress again before saving or playback.";
            BuildWaveform(_originalSamples);
        }

        private void ClearCompressionPlots()
        {
            foreach (var series in CompressionRatioPlotModel.Series)
                if (series is LineSeries line)
                    line.Points.Clear();

            foreach (var series in CompressionSpeedPlotModel.Series)
                if (series is LineSeries line)
                    line.Points.Clear();

            CompressionRatioPlotModel.InvalidatePlot(true);
            CompressionSpeedPlotModel.InvalidatePlot(true);
        }

        private void AddCompressionMetricPoint(int progress, double savingVsOriginalFile, double speedKbPerSecond)
        {
            if (CompressionRatioPlotModel.Series.Count == 0 ||
                CompressionSpeedPlotModel.Series.Count == 0)
                return;

            if (CompressionRatioPlotModel.Series[0] is LineSeries savingSeries)
                savingSeries.Points.Add(new DataPoint(progress, savingVsOriginalFile));
            if (CompressionSpeedPlotModel.Series[0] is LineSeries speedSeries)
                speedSeries.Points.Add(new DataPoint(progress, speedKbPerSecond));

            CompressionRatioPlotModel.InvalidatePlot(true);
            CompressionSpeedPlotModel.InvalidatePlot(true);
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
                    $"Decoded Stream Rate: {props.bitRate / 1000} kbps\n" +
                    $"Decoded Format: {props.codec}";

                var (format, samples, fileSizeBytes) = _audioService.LoadAudio(path);
                _originalFormat        = format;
                _originalSamples       = samples;
                _originalFileSizeBytes = fileSizeBytes;  // store real file size
                _processedSamples      = null;
                _processedFormat       = null;
                _compressedPayload     = null;
                _compressedSettings    = null;
                _compressedFileExtension = GetPreferredCompressedExtension(path);
                _compressedFileBaseName = $"{Path.GetFileNameWithoutExtension(path)}_compressed";

                BuildWaveform(samples);
                StatusMessage        = "Audio loaded successfully.";
                ReportText           = "";
                CompressionRatioText = "N/A";
                SpeedText            = "N/A";
                ProgressValue        = 0;
                ClearCompressionPlots();
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

            CompressionSettings settings;
            float[] samples;
            try
            {
                var prepared = PrepareSamplesForCompression(_originalSamples, _originalFormat);
                samples = prepared.samples;
                settings = CreateSettings(samples.Length, prepared.sampleRate, _originalFormat.Channels);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                return;
            }

            StatusMessage        = "Compressing…";
            ProgressValue        = 0;
            CompressionRatioText = "Calculating...";
            SpeedText            = "0 KB/s";
            ClearCompressionPlots();

            var  sw           = Stopwatch.StartNew();

            // Use the real file size on disk as the "original" size baseline.
            // This gives a meaningful ratio vs what the user actually had on disk.
            long originalSize = _originalFileSizeBytes;
            long estimatedFinalCompressedSize = EstimateCompressedFileSize(settings);
            long decodedPcm16Size = 44 + (long)settings.SampleCount * 2;

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
                    void ReportProgress(int pct, double savingVsOriginalFile, double speedKbPerSecond)
                    {
                        if (token.IsCancellationRequested) return;
                        App.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            ProgressValue        = pct;
                            CompressionRatioText = $"{savingVsOriginalFile:F1}%";
                            SpeedText            = $"{speedKbPerSecond:F0} KB/s";
                            AddCompressionMetricPoint(pct, savingVsOriginalFile, speedKbPerSecond);
                        });
                    }

                    void ReportEncodeProgress(int encodePct)
                    {
                        int overallPct = Math.Clamp(encodePct * 85 / 100, 1, 85);
                        double fraction = encodePct / 100.0;
                        double estimatedCompressedSize = estimatedFinalCompressedSize * fraction;
                        double savingVsOriginalFile = (1.0 - estimatedCompressedSize / originalSize) * 100.0;
                        double processedKb = decodedPcm16Size / 1024.0 * fraction;
                        double speed = processedKb / (sw.Elapsed.TotalSeconds + 0.001);
                        ReportProgress(overallPct, savingVsOriginalFile, speed);
                    }

                    compressedData = Encode(settings, samples, token, ReportEncodeProgress);
                    token.ThrowIfCancellationRequested();

                    double finalSavingEstimate = (1.0 - (double)estimatedFinalCompressedSize / originalSize) * 100.0;
                    double finalSpeedEstimate = decodedPcm16Size / 1024.0 / (sw.Elapsed.TotalSeconds + 0.001);
                    ReportProgress(90, finalSavingEstimate, finalSpeedEstimate);

                    decompressed = Decode(settings, compressedData);
                    token.ThrowIfCancellationRequested();

                    ReportProgress(100, finalSavingEstimate, finalSpeedEstimate);
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
                ClearCompressionPlots();
                return;
            }

            if (bgException != null)
            {
                StatusMessage = $"Compression error: {bgException.Message}";
                return;
            }

            // ── Success ───────────────────────────────────────────────────────
            _processedSamples = decompressed;
            _processedFormat = new WaveFormat(settings.SampleRate, settings.Channels);
            _compressedPayload = compressedData;
            _compressedSettings = settings;

            long compressedSize = compressedData!.Length + CompressedFileHeader.Size;

            double diskSavingRatio = (1.0 - (double)compressedSize / originalSize) * 100.0;
            double pcmSavingRatio = (1.0 - (double)compressedSize / decodedPcm16Size) * 100.0;
            double estimatedBitRate = settings.SampleRate * settings.Channels * settings.Bits / 1000.0;
            double elapsedSeconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            string note = diskSavingRatio < 0
                ? "\nNote: This output is larger than the original file. The source is probably already compressed, like MP3/FLAC. For a smaller saved file, use Adaptive Delta or Delta, Target Sample Rate = 8000, or DPCM/Predictive with 1-2 bits."
                : "";

            ReportText =
                $"--- Compression Report ---\n" +
                $"Algorithm:       {settings.Algorithm}\n" +
                $"Sampling rate:   {settings.SampleRate} Hz\n" +
                $"Bits per sample: {settings.Bits}\n" +
                $"Predictor order: {settings.Order}\n" +
                $"Channels:        {settings.Channels}\n" +
                $"Samples:         {settings.SampleCount:N0}\n" +
                $"Estimated rate:  {estimatedBitRate:F1} kbps\n" +
                $"Original file:   {originalSize / 1024.0:F2} KB  (on disk)\n" +
                $"Decoded PCM:     {decodedPcm16Size / 1024.0:F2} KB  (16-bit equivalent)\n" +
                $"Compressed file: {compressedSize / 1024.0:F2} KB  (with header)\n" +
                $"Saving vs file:  {diskSavingRatio:F1}%\n" +
                $"Saving vs PCM:   {pcmSavingRatio:F1}%\n" +
                $"Time taken:      {sw.Elapsed.TotalSeconds:F2} sec\n" +
                $"Speed:           {originalSize / 1024.0 / elapsedSeconds:F0} KB/s" +
                note;

            StatusMessage        = diskSavingRatio < 0
                ? $"Compressed with {settings.Algorithm}, but output is larger than original. Try 8000 Hz and 1-bit/2-bit settings."
                : $"Compressed with {settings.Algorithm}.";
            ProgressValue        = 100;
            CompressionRatioText = $"{diskSavingRatio:F1}%";
            BuildWaveform(decompressed!);
        }

        // ── Encode / Decode (static — cannot accidentally touch UI thread) ────

        private (float[] samples, int sampleRate) PrepareSamplesForCompression(
            float[] samples, WaveFormat format)
        {
            int targetSampleRate = GetTargetSampleRate(format.SampleRate);
            if (targetSampleRate == format.SampleRate)
                return (samples, format.SampleRate);

            return (
                ResampleLinear(samples, format.SampleRate, targetSampleRate, format.Channels),
                targetSampleRate);
        }

        private int GetTargetSampleRate(int originalSampleRate)
        {
            if (SelectedSampleRateOption == "Original")
                return originalSampleRate;

            if (!int.TryParse(SelectedSampleRateOption, out int targetSampleRate) ||
                targetSampleRate < 8000 ||
                targetSampleRate > 192000)
                throw new InvalidOperationException("Target sample rate is invalid.");

            return targetSampleRate;
        }

        private static float[] ResampleLinear(
            float[] input, int sourceRate, int targetRate, int channels)
        {
            if (sourceRate == targetRate) return input;
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

            int sourceFrames = input.Length / channels;
            int targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (double)targetRate / sourceRate));
            var output = new float[targetFrames * channels];

            for (int frame = 0; frame < targetFrames; frame++)
            {
                double sourcePosition = frame * (double)sourceRate / targetRate;
                int leftFrame = Math.Min((int)sourcePosition, sourceFrames - 1);
                int rightFrame = Math.Min(leftFrame + 1, sourceFrames - 1);
                double fraction = sourcePosition - leftFrame;

                for (int channel = 0; channel < channels; channel++)
                {
                    float left = input[leftFrame * channels + channel];
                    float right = input[rightFrame * channels + channel];
                    output[frame * channels + channel] =
                        (float)(left + (right - left) * fraction);
                }
            }

            return output;
        }

        private CompressionSettings CreateSettings(int sampleCount, int sampleRate, int channels)
        {
            if (sampleCount <= 0)
                throw new InvalidOperationException("Audio file has no samples to compress.");
            if (channels < 1 || channels > 8)
                throw new InvalidOperationException("Only audio with 1 to 8 channels is supported.");

            var algoId = AlgoIdFromName(SelectedAlgorithm);
            int bits = algoId switch
            {
                CompressedFileHeader.AlgoId.MuLaw => 8,
                CompressedFileHeader.AlgoId.Dpcm or CompressedFileHeader.AlgoId.Predictive => QuantizationBits,
                CompressedFileHeader.AlgoId.Delta or CompressedFileHeader.AlgoId.AdaptiveDelta => 1,
                _ => throw new InvalidOperationException($"Unknown algorithm: {SelectedAlgorithm}")
            };

            int order = algoId == CompressedFileHeader.AlgoId.Predictive ? PredictorOrder : 0;

            if (bits < 1 || bits > 8)
                throw new InvalidOperationException("Bits per sample must be between 1 and 8.");
            if (order < 0 || order > 8)
                throw new InvalidOperationException("Predictor order must be between 1 and 8.");
            if (algoId == CompressedFileHeader.AlgoId.Predictive && order == 0)
                throw new InvalidOperationException("Predictor order must be between 1 and 8.");

            return new CompressionSettings(
                SelectedAlgorithm,
                algoId,
                bits,
                sampleCount,
                sampleRate,
                channels,
                order);
        }

        private static long EstimateCompressedFileSize(CompressionSettings settings)
        {
            long payloadSize = settings.AlgoId switch
            {
                CompressedFileHeader.AlgoId.MuLaw => settings.SampleCount,
                CompressedFileHeader.AlgoId.Dpcm or CompressedFileHeader.AlgoId.Predictive =>
                    ((long)settings.SampleCount * settings.Bits + 7) / 8,
                CompressedFileHeader.AlgoId.Delta or CompressedFileHeader.AlgoId.AdaptiveDelta =>
                    ((long)settings.SampleCount + 7) / 8,
                _ => throw new InvalidOperationException($"Unknown algorithm: {settings.Algorithm}")
            };

            return payloadSize + CompressedFileHeader.Size;
        }

        private static byte[] Encode(
            CompressionSettings settings,
            float[] samples,
            CancellationToken token,
            Action<int>? progress = null) =>
            settings.AlgoId switch
            {
                CompressedFileHeader.AlgoId.MuLaw =>
                    MuLawCodec.Encode(samples, token, progress),
                CompressedFileHeader.AlgoId.Dpcm =>
                    DpcmCodec.Encode(samples, settings.Bits, settings.Channels, token, progress),
                CompressedFileHeader.AlgoId.Predictive =>
                    PredictiveCodec.Encode(samples, settings.Bits, settings.Order, settings.Channels, token, progress),
                CompressedFileHeader.AlgoId.Delta =>
                    DeltaModulationCodec.Encode(samples, DeltaStepSize, settings.Channels, token, progress),
                CompressedFileHeader.AlgoId.AdaptiveDelta =>
                    AdaptiveDeltaModulationCodec.Encode(
                        samples, AdaptiveMinStep, AdaptiveMaxStep, AdaptiveAlpha,
                        settings.Channels, token, progress),
                _ => throw new InvalidOperationException($"Unknown algorithm: {settings.Algorithm}")
            };

        private static float[] Decode(CompressionSettings settings, byte[] data) =>
            settings.AlgoId switch
            {
                CompressedFileHeader.AlgoId.MuLaw =>
                    MuLawCodec.Decode(data),
                CompressedFileHeader.AlgoId.Dpcm =>
                    DpcmCodec.Decode(data, settings.Bits, settings.SampleCount, settings.Channels),
                CompressedFileHeader.AlgoId.Predictive =>
                    PredictiveCodec.Decode(
                        data, settings.Bits, settings.SampleCount,
                        settings.Order, settings.Channels),
                CompressedFileHeader.AlgoId.Delta =>
                    DeltaModulationCodec.Decode(
                        data, settings.SampleCount, DeltaStepSize, settings.Channels),
                CompressedFileHeader.AlgoId.AdaptiveDelta =>
                    AdaptiveDeltaModulationCodec.Decode(
                        data, settings.SampleCount, AdaptiveMinStep, AdaptiveMaxStep,
                        AdaptiveAlpha, settings.Channels),
                _ => throw new InvalidOperationException($"Unknown algorithm: {settings.Algorithm}")
            };

        // ── Reset ─────────────────────────────────────────────────────────────

        private void ExecuteReset()
        {
            _processedSamples     = null;
            _processedFormat      = null;
            _compressedPayload    = null;
            _compressedSettings   = null;
            StatusMessage         = "Reset.";
            ReportText            = "";
            CompressionRatioText  = "N/A";
            SpeedText             = "N/A";
            ProgressValue         = 0;
            ClearCompressionPlots();
            _suppressSettingsInvalidation = true;
            try
            {
                SelectedAlgorithm = "Adaptive Delta Modulation";
                SelectedSampleRateOption = "8000";
                QuantizationBits = 2;
                PredictorOrder = 2;
            }
            finally
            {
                _suppressSettingsInvalidation = false;
            }
            if (_originalSamples != null) BuildWaveform(_originalSamples);
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private async void ExecuteSaveCompressed()
        {
            if (_compressedPayload == null || _compressedSettings == null)
            { StatusMessage = "Compress audio or load a compressed file first."; return; }

            var dlg = new SaveFileDialog
            {
                Filter      = CreateCompressedSaveFilter(_compressedFileExtension),
                DefaultExt  = _compressedFileExtension.TrimStart('.'),
                AddExtension = true,
                FileName    = $"{_compressedFileBaseName}{_compressedFileExtension}",
                FilterIndex = 1
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var payload = _compressedPayload;
                var settings = _compressedSettings;
                byte[] header = BuildHeader(settings);

                await Task.Run(() =>
                {
                    using var fs = File.Create(dlg.FileName);
                    fs.Write(header);
                    fs.Write(payload);
                });

                _compressedFileExtension = NormalizeExtension(Path.GetExtension(dlg.FileName));
                _compressedFileBaseName = Path.GetFileNameWithoutExtension(dlg.FileName);
                StatusMessage = $"Saved app-compressed file: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
        }

        private static byte[] BuildHeader(CompressionSettings settings) =>
            CompressedFileHeader.Build(
                settings.AlgoId,
                settings.Bits,
                settings.SampleCount,
                settings.SampleRate,
                settings.Channels,
                settings.Order);

        private static string CreateCompressedSaveFilter(string extension)
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            return $"App-Compressed {label}|*{extension}|All Files|*.*";
        }

        private static string GetPreferredCompressedExtension(string? path)
        {
            string extension = NormalizeExtension(Path.GetExtension(path));
            return extension is ".mp3" or ".wav" or ".aiff" or ".aif" or ".flac"
                ? extension
                : ".acmp";
        }

        private static string NormalizeExtension(string? extension) =>
            string.IsNullOrWhiteSpace(extension)
                ? ".acmp"
                : extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

        // ── Load compressed file ──────────────────────────────────────────────

        private void ExecuteDecompressFromFile()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "App-Compressed Files|*.mp3;*.wav;*.aiff;*.aif;*.flac;*.acmp;*.bin|All Files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(dlg.FileName);

                if (!CompressedFileHeader.TryParse(fileBytes,
                        out var algo, out int bits, out int sampleCount,
                        out int sampleRate, out int channels, out int order))
                {
                    StatusMessage = "File has no valid header. Re-save it with this application.";
                    return;
                }

                int payloadLength = fileBytes.Length - CompressedFileHeader.Size;
                if (!CompressedFileHeader.HasValidPayloadLength(payloadLength, algo, bits, sampleCount))
                {
                    StatusMessage = "Compressed payload length does not match its header.";
                    return;
                }

                var settings = new CompressionSettings(
                    AlgoNameFromId(algo), algo, bits, sampleCount, sampleRate, channels, order);

                _originalFormat = new WaveFormat(sampleRate, channels);
                _processedFormat = new WaveFormat(sampleRate, channels);

                _compressedPayload = new byte[payloadLength];
                Buffer.BlockCopy(fileBytes, CompressedFileHeader.Size,
                                 _compressedPayload, 0, _compressedPayload.Length);
                _compressedSettings = settings;

                _currentFilePath = null;
                _originalSamples = null;
                _processedSamples = null;
                _originalFileSizeBytes = 0;
                _compressedFileExtension = NormalizeExtension(Path.GetExtension(dlg.FileName));
                _compressedFileBaseName = $"{Path.GetFileNameWithoutExtension(dlg.FileName)}_copy";

                _suppressSettingsInvalidation = true;
                try
                {
                    SelectedAlgorithm = settings.Algorithm;
                    QuantizationBits = settings.Bits;
                    PredictorOrder = settings.Order > 0 ? settings.Order : 2;
                    SelectedSampleRateOption = SampleRateOptions.Contains(settings.SampleRate.ToString())
                        ? settings.SampleRate.ToString()
                        : "Original";
                }
                finally
                {
                    _suppressSettingsInvalidation = false;
                }
                WaveformModel?.Series.Clear();
                WaveformModel?.InvalidatePlot(true);
                AudioInfo =
                    $"Compressed file: {Path.GetFileName(dlg.FileName)}\n" +
                    $"Size: {fileBytes.Length / 1024.0:F2} KB\n" +
                    $"Algorithm: {settings.Algorithm}\n" +
                    $"Sample Rate: {settings.SampleRate} Hz\n" +
                    $"Channels: {settings.Channels}\n" +
                    $"Samples: {settings.SampleCount:N0}";
                StatusMessage = $"Loaded {Path.GetFileName(dlg.FileName)} " +
                                $"({settings.SampleCount:N0} samples, {settings.Algorithm}). " +
                                "Click 'Decompress Loaded'.";
            }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        }

        // ── Decompress loaded .bin ────────────────────────────────────────────

        private void ExecuteDecompressLoaded()
        {
            if (_compressedPayload == null || _compressedSettings == null)
            { StatusMessage = "No compressed file loaded."; return; }

            try
            {
                float[] decompressed = Decode(_compressedSettings, _compressedPayload);

                _processedSamples = decompressed;
                _processedFormat = new WaveFormat(_compressedSettings.SampleRate, _compressedSettings.Channels);
                BuildWaveform(decompressed);
                StatusMessage = "Decompressed. Click 'Play Decompressed' to listen.";
            }
            catch (Exception ex) { StatusMessage = $"Decompression failed: {ex.Message}"; }
        }

        // ── Play decompressed ─────────────────────────────────────────────────

        private void ExecutePlayDecompressed()
        {
            if (_processedSamples == null || _processedFormat == null)
            { StatusMessage = "No decompressed audio available."; return; }
            _audioService.PlayFromMemory(
                _processedSamples,
                _processedFormat.SampleRate,
                _processedFormat.Channels);
            StatusMessage = "Playing decompressed audio.";
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
