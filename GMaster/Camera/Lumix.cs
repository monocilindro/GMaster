﻿using System.Collections.Immutable;

namespace GMaster.Camera
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Serialization;
    using Annotations;
    using LumixResponces;
    using Windows.Storage.Streams;
    using Windows.UI.Xaml;
    using Windows.Web.Http.Filters;
    using Windows.Web.Http.Headers;
    using HttpClient = Windows.Web.Http.HttpClient;
    using HttpResponseMessage = Windows.Web.Http.HttpResponseMessage;

    public struct TextBinValue
    {
        public TextBinValue(string text, int bin)
        {
            Text = text;
            Bin = bin;
        }

        public string Text { get; }

        public int Bin { get; }

        public bool Equals(TextBinValue other)
        {
            return string.Equals(Text, other.Text) && Bin == other.Bin;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            return obj is TextBinValue && Equals((TextBinValue) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Text?.GetHashCode() ?? 0) * 397) ^ Bin;
            }
        }
    }

    public partial class Lumix : INotifyPropertyChanged
    {
        private readonly Uri baseUri;

        private readonly HttpClient camcgi;

        private readonly object messageRecieving = new object();

        private readonly DispatcherTimer stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        private readonly SemaphoreSlim stateUpdatingSem = new SemaphoreSlim(1);

        public async Task SendMenuItem(CameraMenuItem value)
        {
            await Get<BaseRequestResult>(new Dictionary<string, string>
                {
                    { "mode", value.Command },
                    { "type", value.CommandType },
                    { "value", value.Value }
                });
        }

        private MemoryStream currentImageStream;

        private byte lastByte;

        public MenuSet MenuSet { get; private set; }

        internal Lumix(DeviceInfo device)
        {
            Device = device;
            baseUri = new Uri($"http://{CameraHost}/cam.cgi");
            stateTimer.Tick += StateTimer_Tick;

            var rootFilter = new HttpBaseProtocolFilter();
            rootFilter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            rootFilter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;

            camcgi = new HttpClient(rootFilter);
            camcgi.DefaultRequestHeaders.Accept.Clear();
            camcgi.DefaultRequestHeaders.Accept.Add(new HttpMediaTypeWithQualityHeaderValue("application/xml"));
        }

        public delegate void DisconnectedDelegate(Lumix camera, bool stillAvailable);

        public event DisconnectedDelegate Disconnected;

        public event PropertyChangedEventHandler PropertyChanged;

        public string CameraHost => Device.Host;

        public DeviceInfo Device { get; }

        public TextBinValue CurrentIso { get; private set; }

        public TextBinValue CurrentShutter { get; private set; }

        public TextBinValue CurrentAperture { get; private set; }

        public CameraMode CurrentCameraMode { get; private set; }

        private static IReadOnlyDictionary<int, CameraMode> IntToCameraMode =
            Enum.GetValues(typeof(CameraMode)).Cast<CameraMode>().ToImmutableDictionary(m => (int)m);

        public bool IsConnected { get; private set; }

        public bool IsLimited => MenuSet == null;

        public Stream LiveViewFrame { get; private set; }

        public RecState RecState { get; private set; } = RecState.Unknown;

        public CameraState State { get; private set; }

        public string Udn => Device.Udn;

        public async Task Capture()
        {
            await Get<BaseRequestResult>("?mode=camcmd&value=capture");
            await Get<BaseRequestResult>("?mode=camcmd&value=capture_cancel");
        }

        public async Task Connect(int liveviewport, string lang)
        {
            try
            {
                currentImageStream = null;
                lastByte = 0;

                await ReadMenuSet(lang);
                await SwitchToRec();
                await UpdateState();
                stateTimer.Start();
                await Get<BaseRequestResult>($"?mode=startstream&value={liveviewport}");

                IsConnected = true;
                OnPropertyChanged(nameof(IsConnected));
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        public async Task Disconnect()
        {
            try
            {
                IsConnected = false;
                stateTimer.Stop();
                try
                {
                    await Get<BaseRequestResult>("?mode=stopstream");
                    Disconnected?.Invoke(this, true);
                }
                catch (Exception)
                {
                    Disconnected?.Invoke(this, false);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            finally
            {
                OnPropertyChanged(nameof(IsConnected));
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Lumix)obj);
        }

        public override int GetHashCode()
        {
            return Udn?.GetHashCode() ?? 0;
        }

        public void ManagerRestarted()
        {
            lock (messageRecieving)
            {
                currentImageStream = null;
                lastByte = 0;
            }
        }

        public void ProcessMessage(ArraySegment<byte> buf)
        {
            lock (messageRecieving)
            {
                foreach (var b in buf)
                {
                    ProcessByte(b);
                }
            }
        }

        public void ProcessMessage(DataReader reader)
        {
            lock (messageRecieving)
            {
                using (reader)
                {
                    while (reader.UnconsumedBufferLength > 0)
                    {
                        var curByte = reader.ReadByte();
                        ProcessByte(curByte);
                    }
                }
            }
        }

        public async Task ReadCurMenu()
        {
            var allmenuString = await GetString("?mode=getinfo&type=curmenu");
            var result = ReadResponse<MenuSetRuquestResult>(allmenuString);

            try
            {
                MenuSet = MenuSet.TryParseMenuSet(result.MenuSet, CultureInfo.CurrentCulture.TwoLetterISOLanguageName);
            }
            catch (AggregateException ex)
            {
                Log.Error(new Exception("Cannot parse MenuSet.\r\n" + allmenuString, ex));
            }

            await Get<BaseRequestResult>("?mode=getinfo&type=curmenu");
            RecState = RecState.Unknown;
            OnPropertyChanged(nameof(RecState));
        }

        public async Task RecStart()
        {
            await Get<BaseRequestResult>("?mode=camcmd&value=video_recstart");
            RecState = RecState.Unknown;
            OnPropertyChanged(nameof(RecState));
        }

        public async Task RecStop()
        {
            await Get<BaseRequestResult>("?mode=camcmd&value=video_recstop");
            RecState = RecState.Unknown;
            OnPropertyChanged(nameof(RecState));
        }

        public async Task SwitchToRec()
        {
            await Get<BaseRequestResult>("?mode=camcmd&value=recmode");
        }

        protected bool Equals(Lumix other)
        {
            return Equals(Udn, other.Udn);
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged(string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnSelfChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string PropertyString(object value)
        {
            switch (value)
            {
                case null:
                    return null;

                case Enum en:
                    var attribute = typeof(Enum)
                        .GetMember(en.ToString())
                        .First()
                        .GetCustomAttribute<XmlElementAttribute>();
                    return attribute?.ElementName ?? en.ToString();

                default:
                    return value.ToString();
            }
        }

        private static async Task<TResponse> ReadResponse<TResponse>(HttpResponseMessage response)
        {
            using (var content = response.Content)
            using (var str = await content.ReadAsInputStreamAsync())
            {
                var serializer = new XmlSerializer(typeof(TResponse));
                return (TResponse)serializer.Deserialize(str.AsStreamForRead());
            }
        }

        private static TResponse ReadResponse<TResponse>(string str)
        {
            var serializer = new XmlSerializer(typeof(TResponse));
            return (TResponse)serializer.Deserialize(new StringReader(str));
        }

        private async Task<TResponse> Get<TResponse>(string path)
            where TResponse : BaseRequestResult
        {
            var uri = new Uri(baseUri, path);
            using (var response = await camcgi.GetAsync(uri))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new LumixException("Request failed: " + path);
                }

                var product = await ReadResponse<TResponse>(response);
                if (product.Result != "ok")
                {
                    throw new LumixException($"Not ok result\r\nRequest: {path}\r\n{await response.Content.ReadAsStringAsync()}");
                }

                return product;
            }
        }

        private async Task<string> GetString(string path)
        {
            var uri = new Uri(baseUri, path);
            using (var response = await camcgi.GetAsync(uri))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new LumixException("Request failed: " + path);
                }

                return await response.Content.ReadAsStringAsync();
            }
        }

        private async Task<TResponse> Get<TResponse>(Dictionary<string, string> parameters)
            where TResponse : BaseRequestResult
        {
            var uri = new Uri(baseUri, "?" + string.Join("&", parameters.Select(p => p.Key + "=" + p.Value)));
            using (var response = await camcgi.GetAsync(uri))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new LumixException("Request failed: ");
                }

                var product = await ReadResponse<TResponse>(response);
                if (product.Result != "ok")
                {
                    throw new LumixException($"Not ok result\r\nRequest: \r\n{await response.Content.ReadAsStringAsync()}");
                }

                return product;
            }
        }

        private MemoryStream offframeBytes;

        private void ProcessByte(byte curByte)
        {
            currentImageStream?.WriteByte(curByte);

            offframeBytes?.WriteByte(curByte);

            if (lastByte == 0xff)
            {
                if (curByte == 0xd8)
                {
                    ProcessOffframeBytes();
                    offframeBytes = null;
                    currentImageStream = new MemoryStream(32768);
                    currentImageStream.WriteByte(0xff);
                    currentImageStream.WriteByte(0xd8);
                }
                else if (currentImageStream != null && curByte == 0xd9)
                {
                    LiveViewFrame = currentImageStream;
                    App.RunAsync(() => OnPropertyChanged(nameof(LiveViewFrame)));

                    currentImageStream = null;

                    offframeBytes = new MemoryStream();
                }
            }

            lastByte = curByte;
        }

        public int CurrentIsoBin { get; private set; }

        private void ProcessOffframeBytes()
        {
            try
            {
                if (offframeBytes == null || offframeBytes.Length < 130 || !offframeBytes.TryGetBuffer(out var array))
                {
                    return;
                }

                App.RunAsync(() =>
                {
                    try
                    {
                        var newIso = MenuSet.GetIso(array);
                        if (!Equals(newIso, CurrentIso))
                        {
                            CurrentIso = newIso;
                            OnPropertyChanged(nameof(CurrentIso));
                        }
                    }
                    catch (KeyNotFoundException e)
                    {
                        Log.Error(new Exception("Cannot parse off-frame bytes for camera: " + Device.ModelName, e));
                        CurrentIso = new TextBinValue("!", -1);
                        OnPropertyChanged(nameof(CurrentIso));
                    }

                    try
                    {
                        var newShutter = MenuSet.GetShutter(array);
                        if (!Equals(newShutter, CurrentShutter))
                        {
                            CurrentShutter = newShutter;
                            OnPropertyChanged(nameof(CurrentShutter));
                        }
                    }
                    catch (KeyNotFoundException e)
                    {
                        Log.Error(new Exception("Cannot parse off-frame bytes for camera: " + Device.ModelName, e));
                        CurrentShutter = new TextBinValue("!", -1);
                        OnPropertyChanged(nameof(CurrentShutter));
                    }

                    try
                    {
                        var newAperture = MenuSet.GetAperture(array);
                        if (!Equals(newAperture, CurrentAperture))
                        {
                            CurrentAperture = newAperture;
                            OnPropertyChanged(nameof(CurrentAperture));
                        }
                    }
                    catch (KeyNotFoundException e)
                    {
                        Log.Error(new Exception("Cannot parse off-frame bytes for camera: " + Device.ModelName, e));
                        CurrentAperture = new TextBinValue("!", -1);
                        OnPropertyChanged(nameof(CurrentAperture));
                    }

                    var newMode = IntToCameraMode.TryGetValue(array.Array[92], out var mode) ? mode : CameraMode.Unknown;
                    if (newMode != CurrentCameraMode)
                    {
                        CurrentCameraMode = newMode;
                        OnPropertyChanged(nameof(CurrentCameraMode));
                        OnCameraModeUpdated();
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error(new Exception("Cannot parse off-frame bytes for camera: " + Device.ModelName, e));
            }
        }

        public int CurrentShutterBin { get; private set; }

        public bool CanChangeAperture { get; private set; } = true;

        public bool CanChangeShutter { get; private set; } = true;

        private void OnCameraModeUpdated()
        {
            CanChangeAperture = CurrentCameraMode != CameraMode.S;
            CanChangeShutter = CurrentCameraMode != CameraMode.A;
            OnPropertyChanged(nameof(CanChangeAperture));
            OnPropertyChanged(nameof(CanChangeShutter));
        }

        private async Task ReadMenuSet(string lang)
        {
            var allmenuString = await GetString("?mode=getinfo&type=allmenu");
            var result = ReadResponse<MenuSetRuquestResult>(allmenuString);

            try
            {
                MenuSet = MenuSet.TryParseMenuSet(result.MenuSet, lang);
            }
            catch (AggregateException ex)
            {
                Log.Error(new Exception("Cannot parse MenuSet.\r\n" + allmenuString, ex));
            }
        }

        private async void StateTimer_Tick(object sender, object e)
        {
            await stateUpdatingSem.WaitAsync();
            {
                try
                {
                    if (IsConnected)
                    {
                        await UpdateState();
                    }
                }
                catch (Exception)
                {
                    await Disconnect();
                }
                finally
                {
                    stateUpdatingSem.Release();
                }
            }
        }

        private async Task<CameraState> UpdateState()
        {
            var newState = await Get<CameraStateRequestResult>("?mode=getstate");
            if (State != null && newState.State.Equals(State))
            {
                return State;
            }

            State = newState.State ?? throw new NullReferenceException();

            RecState = State.Rec == OnOff.On ? RecState.Started : RecState.Stopped;

            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(RecState));
            return State;
        }
    }
}