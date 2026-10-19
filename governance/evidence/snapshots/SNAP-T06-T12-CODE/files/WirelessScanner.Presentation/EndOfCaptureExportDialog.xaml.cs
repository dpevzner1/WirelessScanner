using System;
using System.Windows;

namespace WirelessScanner.Presentation;

public partial class EndOfCaptureExportDialog : Window
{
    /// <summary>
    /// The export format chosen by the user: "csv", "json", "pdf", or null if skipped.
    /// </summary>
    public string? ChosenFormat { get; private set; }

    public EndOfCaptureExportDialog()
    {
        InitializeComponent();
    }

    public void SetSessionInfo(string sessionName, string facility, TimeSpan duration)
    {
        TxtSessionName.Text = sessionName;
        TxtFacility.Text = string.IsNullOrEmpty(facility) ? "N/A" : facility;

        if (duration.TotalMinutes < 1)
        {
            TxtDuration.Text = $"{duration.TotalSeconds:F0} seconds";
        }
        else
        {
            TxtDuration.Text = $"{duration.TotalMinutes:F1} minutes";
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        ChosenFormat = "csv";
        DialogResult = true;
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        ChosenFormat = "json";
        DialogResult = true;
    }

    private void OnExportPdf(object sender, RoutedEventArgs e)
    {
        ChosenFormat = "pdf";
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        ChosenFormat = null;
        DialogResult = false;
    }
}
