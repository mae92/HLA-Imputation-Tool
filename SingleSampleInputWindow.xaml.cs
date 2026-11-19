using DocumentFormat.OpenXml.Wordprocessing;
using HLAImputation.Models;
using HLAImputation.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HLAImputation
{
    public partial class SingleSampleInputWindow : Window
    {
        private readonly HlaTextParser _parser = new();
        private readonly HlaRelevantTextSelector _textSelector = new();
        private readonly PdfTextExtractor _pdfExtractor = new();
        private readonly PdfOcrService _pdfOcrService = new();
        private readonly SingleSampleRecordBuilder _recordBuilder = new();
        
        

        public InputRecord? ResultRecord { get; private set; }

        public SingleSampleInputWindow()
        {
            InitializeComponent();

            RaceComboBox.ItemsSource = new[]
            {
                "",
                "AFA",
                "API",
                "CAU",
                "HIS",
                "NAM"
            };
        }

        private async void PasteScreenshot_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                // This screenshot represents a new report,
                // so remove the previous patient's typing.
                ClearTypingFields();

                byte[]? imageBytes =
                    ClipboardImageHelper.GetClipboardImageAsPng();

                if (imageBytes == null)
                {
                    MessageBox.Show(
                        "The clipboard does not contain an image.");
                    return;
                }

                StatusText.Text = "Reading screenshot...";

                using var ocr = new ImageOcrService();

                string text =
                    await ocr.ExtractTextAsync(imageBytes);

                ExtractedTextBox.Text = text;

                ParseAndPopulate(
                    text,
                    "Clipboard screenshot");

                StatusText.Text =
                    "Screenshot processed. Review the extracted typing.";
            }
            catch (Exception ex)
            {
                ShowImportError(ex);
            }
        }

        private async void OpenImage_Click(
            object sender,
            RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter =
                    "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff"
            };

            if (dialog.ShowDialog() != true)
                return;

            await ImportImageAsync(dialog.FileName);
        }

        private async Task ImportImageAsync(
    string path)
        {
            try
            {
                // A new report is being loaded.
                // Remove typing left from the previous report.
                ClearTypingFields();

                StatusText.Text =
                    "Reading " +
                    Path.GetFileName(path) +
                    "...";

                using var ocr =
                    new ImageOcrService();

                string text =
                    await ocr.ExtractTextAsync(
                        path);

                // Show ALL extracted text to the user.
                ExtractedTextBox.Text =
                    text;

                // Parse the extracted text and populate
                // the HLA fields.
                ParseAndPopulate(
                    text,
                    Path.GetFileName(path));

                StatusText.Text =
                    "Image processed. Review the extracted typing.";
            }
            catch (Exception ex)
            {
                ShowImportError(ex);
            }
        }

        private async void OpenPdf_Click(
            object sender,
            RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "PDF files|*.pdf"
            };

            if (dialog.ShowDialog() != true)
                return;

            await ImportPdfAsync(dialog.FileName);
        }

        private async Task ImportPdfAsync(string path)
        {
            try
            {
                // Clear typing from the previously loaded report.
                ClearTypingFields();

                StatusText.Text =
                    $"Reading {Path.GetFileName(path)}...";

                string nativeText =
                    _pdfExtractor.ExtractNativeText(path);

                string selectedText;

                if (_pdfExtractor.ContainsUsableHlaText(nativeText))
                {
                    selectedText = nativeText;

                    StatusText.Text =
                        "Embedded PDF text found. Searching HLA result sections...";
                }
                else
                {
                    StatusText.Text =
                        "No usable embedded text found. Rendering PDF pages for OCR...";

                    selectedText =
                        await _pdfOcrService.ExtractTextAsync(
                            path,
                            maximumPages: 6);
                }

                ExtractedTextBox.Text = selectedText;

                ParseAndPopulate(
                    selectedText,
                    Path.GetFileName(path));

                StatusText.Text =
                    "PDF processed. Review every extracted typing value.";
            }
            catch (Exception ex)
            {
                ShowImportError(ex);
            }
        }

        private void OpenTextFile_Click(
            object sender,
            RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Text files|*.txt;*.csv|All files|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                ClearTypingFields();

                string text =
                    File.ReadAllText(dialog.FileName);

                ExtractedTextBox.Text = text;

                ParseAndPopulate(
                    text,
                    Path.GetFileName(dialog.FileName));

                StatusText.Text =
                    "Text file processed. Review the extracted typing.";
            }
            catch (Exception ex)
            {
                ShowImportError(ex);
            }
        }

        private void ParseText_Click(
            object sender,
            RoutedEventArgs e)
        {
            ParseAndPopulate(
                ExtractedTextBox.Text,
                "Edited extracted text");
        }

        private void ParseAndPopulate(
            string text,
            string source)
        {
            string relevantText =
                _textSelector.SelectRelevantText(text);

            ParsedHlaTyping parsed =
                _parser.Parse(
                    relevantText,
                    source);

            if (!string.IsNullOrWhiteSpace(parsed.SampleId))
                SampleIdTextBox.Text = parsed.SampleId;

            if (!string.IsNullOrWhiteSpace(parsed.Race))
                RaceComboBox.Text = parsed.Race;

            if (!string.IsNullOrWhiteSpace(parsed.PatientType))
                PatientTypeTextBox.Text = parsed.PatientType;

            SetPair(parsed, "A", A1TextBox, A2TextBox);
            SetPair(parsed, "B", B1TextBox, B2TextBox);
            SetPair(parsed, "C", C1TextBox, C2TextBox);
            SetPair(parsed, "DRB1", DRB11TextBox, DRB12TextBox);
            SetPair(parsed, "DRB3", DRB31TextBox, DRB32TextBox);
            SetPair(parsed, "DRB4", DRB41TextBox, DRB42TextBox);
            SetPair(parsed, "DRB5", DRB51TextBox, DRB52TextBox);
            SetPair(parsed, "DQA1", DQA11TextBox, DQA12TextBox);
            SetPair(parsed, "DQB1", DQB11TextBox, DQB12TextBox);
            SetPair(parsed, "DPA1", DPA11TextBox, DPA12TextBox);
            SetPair(parsed, "DPB1", DPB11TextBox, DPB12TextBox);


            // Add equivalent calls for all remaining fields.

            WarningsTextBox.Text =
                parsed.Warnings.Count == 0
                    ? "No parser warnings."
                    : string.Join(
                        Environment.NewLine,
                        parsed.Warnings.Select(x => "• " + x));
        }

        private static void SetPair(
    ParsedHlaTyping parsed,
    string locus,
    TextBox first,
    TextBox second)
        {
            if (!parsed.AllelesByLocus.TryGetValue(
                    locus,
                    out var values))
            {
                return;
            }

            if (values.Count > 0 &&
                string.IsNullOrWhiteSpace(first.Text))
            {
                first.Text =
                    values[0];
            }

            if (values.Count > 1 &&
                string.IsNullOrWhiteSpace(second.Text))
            {
                second.Text =
                    values[1];
            }
        }

        private void ClearTypingFields()
        {
            // Clear patient/sample metadata.
            SampleIdTextBox.Clear();

            RaceComboBox.SelectedIndex = -1;
            RaceComboBox.Text = "";

            PatientTypeTextBox.Clear();

            // Clear HLA typing.
            TextBox[] boxes =
            {
                A1TextBox,
                A2TextBox,

                B1TextBox,
                B2TextBox,

                C1TextBox,
                C2TextBox,

                DRB11TextBox,
                DRB12TextBox,

                DRB31TextBox,
                DRB32TextBox,

                DRB41TextBox,
                DRB42TextBox,

                DRB51TextBox,
                DRB52TextBox,

                DQA11TextBox,
                DQA12TextBox,

                DQB11TextBox,
                DQB12TextBox,

                DPA11TextBox,
                DPA12TextBox,

                DPB11TextBox,
                DPB12TextBox
            };

            foreach (TextBox box in boxes)
            {
                box.Clear();
            }

            WarningsTextBox.Clear();
        }

        private void LoadSample_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var loci = new Dictionary<string, string[]>(
                    StringComparer.OrdinalIgnoreCase)
            {
                { "A", new[] { A1TextBox.Text, A2TextBox.Text } },
                { "B", new[] { B1TextBox.Text, B2TextBox.Text } },
                { "C", new[] { C1TextBox.Text, C2TextBox.Text } },

                { "DRB1", new[] { DRB11TextBox.Text, DRB12TextBox.Text } },

                { "DQA1", new[] { DQA11TextBox.Text, DQA12TextBox.Text } },
                { "DQB1", new[] { DQB11TextBox.Text, DQB12TextBox.Text } },

                { "DPA1", new[] { DPA11TextBox.Text, DPA12TextBox.Text } },
                { "DPB1", new[] { DPB11TextBox.Text, DPB12TextBox.Text } }
            };

                ResultRecord = _recordBuilder.Build(
                    SampleIdTextBox.Text,
                    RaceComboBox.Text,
                    PatientTypeTextBox.Text,
                    loci,
                    new[]
                    {
                        DRB31TextBox.Text,
                        DRB32TextBox.Text
                    },
                    new[]
                    {
                        DRB41TextBox.Text,
                        DRB42TextBox.Text
                    },
                    new[]
                    {
                        DRB51TextBox.Text,
                        DRB52TextBox.Text
                    });

                ValidateBeforeClosing(ResultRecord);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Cannot Load Sample",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }


        private void Window_DragOver(
    object sender,
    DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            string[] files =
                (string[])e.Data.GetData(DataFormats.FileDrop);

            e.Effects = files.Length == 1
                ? DragDropEffects.Copy
                : DragDropEffects.None;

            e.Handled = true;
        }

        private async void Window_Drop(
            object sender,
            DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] files =
                (string[])e.Data.GetData(DataFormats.FileDrop);

            if (files.Length != 1)
            {
                MessageBox.Show(
                    "Please drop one report at a time.");
                return;
            }

            string path = files[0];
            string extension =
                Path.GetExtension(path).ToLowerInvariant();

            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".tif":
                case ".tiff":
                    await ImportImageAsync(path);
                    break;

                case ".pdf":
                    await ImportPdfAsync(path);
                    break;

                case ".txt":
                case ".csv":
                    string text = File.ReadAllText(path);
                    ExtractedTextBox.Text = text;
                    ParseAndPopulate(text, Path.GetFileName(path));
                    break;

                default:
                    MessageBox.Show(
                        "Unsupported file type: " + extension);
                    break;
            }
        }


        private static void ValidateBeforeClosing(
            InputRecord record)
        {
            int alleleCount =
                record.Loci
                    .Where(kv => kv.Key != "DRB345")
                    .SelectMany(kv => kv.Value)
                    .Count(x => !string.IsNullOrWhiteSpace(x));

            if (alleleCount == 0)
            {
                throw new InvalidOperationException(
                    "At least one HLA value must be entered.");
            }
        }

        private void Cancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowImportError(Exception ex)
        {
            StatusText.Text =
                "Import failed.";

            MessageBox.Show(
                ex.Message,
                "Import Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}