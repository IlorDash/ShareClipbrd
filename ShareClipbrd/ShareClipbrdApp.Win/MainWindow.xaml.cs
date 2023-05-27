﻿using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GuardNet;
using ShareClipbrd.Core.Clipboard;
using ShareClipbrd.Core.Helpers;
using ShareClipbrd.Core.Services;
using ShareClipbrdApp.Win.Helpers;
using ShareClipbrdApp.Win.Properties;

namespace ShareClipbrdApp.Win {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        public System.Drawing.Rectangle Bounds { get { return new System.Drawing.Rectangle((int)Left, (int)Top, (int)Width, (int)Height); } }

        readonly IDataClient dataClient;
        readonly IDataServer dataServer;

        public MainWindow(
            IDataClient dataClient,
            IDataServer dataServer) {
            Guard.NotNull(dataClient, nameof(dataClient));
            Guard.NotNull(dataServer, nameof(dataServer));

            this.dataClient = dataClient;
            this.dataServer = dataServer;

            InitializeComponent();

        }

        void Window_Initialized(object sender, System.EventArgs e) {
            WindowsHelper.LoadLocation(Settings.Default.MainFormLocation, this);
            Height = SystemParameters.FullPrimaryScreenHeight / 40;
            Width = SystemParameters.FullPrimaryScreenWidth / 40;
            dataServer.Start();
            edHostAddress.Text = Settings.Default.HostAddress;
            edPartnerAddress.Text = Settings.Default.PartnerAddress;
            edCompressionLevel.ItemsSource = CompressionLevelHelper.Names.Values;
            edCompressionLevel.Text = CompressionLevelHelper.GetName(Settings.Default.Compression);
        }

        void Window_Closed(object sender, System.EventArgs e) {
            dataServer.Stop();
            Settings.Default.MainFormLocation = new System.Drawing.Point((int)Left, (int)Top);
            Settings.Default.Save();
        }

        void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            this.DragMove();
        }

        void MenuItemClose_Click(object sender, RoutedEventArgs e) {
            this.Close();
        }

        async void Window_KeyDown(object sender, KeyEventArgs e) {
            if(e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) {
                await TransmitClipboard();
            }
        }

        async void MenuItemPaste_Click(object sender, RoutedEventArgs e) {
            await TransmitClipboard();
        }

        void Window_Activated(object sender, System.EventArgs e) {
            Border.BorderBrush = Brushes.Aqua;
            Border.BorderThickness = new Thickness(2);
        }

        void Window_Deactivated(object sender, System.EventArgs e) {
            Border.BorderBrush = Brushes.DarkGoldenrod;
            Border.BorderThickness = new Thickness(1);
        }

        async Task TransmitClipboard() {
            await using(ProcessIndicator.Indicate(this, ProcessIndicator.Mode.Send)) {
                var clipboardData = new ClipboardData();
                if(System.Windows.Clipboard.ContainsFileDropList()) {
                    await dataClient.SendFileDropList(System.Windows.Clipboard.GetFileDropList());
                } else if(System.Windows.Clipboard.ContainsImage()) {

                } else if(System.Windows.Clipboard.ContainsAudio()) {

                } else {
                    var dataObject = System.Windows.Clipboard.GetDataObject();
                    clipboardData.Serialize(dataObject.GetFormats(), dataObject.GetData);
                    await dataClient.SendData(clipboardData);
                }
            }
        }        

        private void edHostAddress_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
            Settings.Default.HostAddress = edHostAddress.Text;
        }

        private void edPartnerAddress_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
            Settings.Default.PartnerAddress = edPartnerAddress.Text;
        }

        private void edCompressionLevel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
            Settings.Default.Compression = (int)CompressionLevelHelper.GetValue(edCompressionLevel.SelectedItem as string);
        }
    }
}
