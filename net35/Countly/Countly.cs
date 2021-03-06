﻿/*
Copyright (c) 2012, 2013, 2014 Countly

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using CountlySDK.Entities;
using CountlySDK.Entitites;
using CountlySDK.Helpers;
using CountlySDK.Server.Responses;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CountlySDK
{
    /// <summary>
    /// This class is the public API for the Countly .NET 3.5 SDK.
    /// </summary>
    public static class Countly
    {
        private const string sdkVersion = "1.0";

        // How often update session is sent
        private const int updateInterval = 60;

        // Server url provided by a user
        private static string ServerUrl;

        // Application key provided by a user
        private static string AppKey;

        // Application key provided by a user
        private static string AppVersion;

        // Indicates sync process with a server
        private static bool uploadInProgress;

        // File that stores events objects
        private const string eventsFilename = "events.xml";
        // File that stores sessions objects
        private const string sessionsFilename = "sessions.xml";
        // File that stores exceptions objects
        private const string exceptionsFilename = "exceptions.xml";
        // File that stores user details object
        private const string userDetailsFilename = "userdetails.xml";

        // Used for thread-safe operations
        private static object sync = new object();

        private static List<CountlyEvent> events;
        // Events queue
        private static List<CountlyEvent> Events
        {
            get
            {
                lock (sync)
                {
                    if (events == null)
                    {
                        events = Storage.LoadFromFile<List<CountlyEvent>>(eventsFilename);

                        if (events == null)
                        {
                            events = new List<CountlyEvent>();
                        }
                    }
                }

                return events;
            }
        }

        private static List<SessionEvent> sessions;
        // Session queue
        private static List<SessionEvent> Sessions
        {
            get
            {
                lock (sync)
                {
                    if (sessions == null)
                    {
                        sessions = Storage.LoadFromFile<List<SessionEvent>>(sessionsFilename);

                        if (sessions == null)
                        {
                            sessions = new List<SessionEvent>();
                        }
                    }
                }

                return sessions;
            }
        }

        private static List<ExceptionEvent> exceptions;
        // Exceptions queue
        private static List<ExceptionEvent> Exceptions
        {
            get
            {
                lock (sync)
                {
                    if (exceptions == null)
                    {
                        exceptions = Storage.LoadFromFile<List<ExceptionEvent>>(exceptionsFilename);

                        if (exceptions == null)
                        {
                            exceptions = new List<ExceptionEvent>();
                        }
                    }
                }

                return exceptions;
            }
        }

        private static CountlyUserDetails userDetails;
        // User details info
        public static CountlyUserDetails UserDetails
        {
            get
            {
                lock (sync)
                {
                    if (userDetails == null)
                    {
                        userDetails = Storage.LoadFromFile<CountlyUserDetails>(userDetailsFilename);

                        if (userDetails == null)
                        {
                            userDetails = new CountlyUserDetails();
                        }

                        userDetails.UserDetailsChanged += OnUserDetailsChanged;
                    }
                }

                return userDetails;
            }

        }

        private static string breadcrumb = String.Empty;

        // Start session timestamp
        private static DateTime startTime;
        // Update session timer
        private static DispatcherTimer Timer;

        /// <summary>
        /// Determines if Countly debug messages are displayed to Output window
        /// </summary>
        public static bool IsLoggingEnabled { get; set; }

        /// <summary>
        /// Saves events to the storage
        /// </summary>
        private static void SaveEvents()
        {
            Storage.SaveToFile(eventsFilename, Events);
        }

        /// <summary>
        /// Saves sessions to the storage
        /// </summary>
        private static void SaveSessions()
        {
            Storage.SaveToFile(sessionsFilename, Sessions);
        }

        /// <summary>
        /// Saves exceptions to the storage
        /// </summary>
        private static void SaveExceptions()
        {
            Storage.SaveToFile(exceptionsFilename, Exceptions);
        }

        /// <summary>
        /// Saves user details info to the storage
        /// </summary>
        private static void SaveUserDetails()
        {
            Storage.SaveToFile(userDetailsFilename, UserDetails);
        }

        /// <summary>
        /// Starts Countly tracking session.
        /// Call from your entry point.
        /// Must be called before other SDK methods can be used.
        /// </summary>
        /// <param name="serverUrl">URL of the Countly server to submit data to; use "https://cloud.count.ly" for Countly Cloud</param>
        /// <param name="appKey">app key for the application being tracked; find in the Countly Dashboard under Management > Applications</param>
        /// <param name="appVersion">Application version</param>
        public static async Task StartSession(string serverUrl, string appKey, string appVersion)
        {
            if (String.IsNullOrEmpty(serverUrl))
            {
                throw new ArgumentException("invalid server url");
            }

            if (String.IsNullOrEmpty(appKey))
            {
                throw new ArgumentException("invalid application key");
            }

            ServerUrl = serverUrl;
            AppKey = appKey;
            AppVersion = appVersion;

            startTime = DateTime.Now;

            Timer = new DispatcherTimer();
            Timer.Interval = TimeSpan.FromSeconds(updateInterval);
            Timer.Tick += UpdateSession;
            Timer.Start();

            await AddSessionEvent(new BeginSession(AppKey, Device.DeviceId, sdkVersion, new Metrics(Device.OS, Device.OSVersion, Device.DeviceName, Device.Resolution, appVersion)));
        }

        /// <summary>
        /// Starts Countly tracking session.
        /// Call from your entry point.
        /// Must be called before other SDK methods can be used.
        /// </summary>
        /// <param name="serverUrl">URL of the Countly server to submit data to; use "https://cloud.count.ly" for Countly Cloud</param>
        /// <param name="appKey">app key for the application being tracked; find in the Countly Dashboard under Management > Applications</param>
        public static async Task StartSession(string serverUrl, string appKey)
        {
            await StartSession(serverUrl, appKey, String.Empty);
        }

        /// <summary>
        /// Sends session duration. Called automatically each <updateInterval> seconds
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void UpdateSession(object sender, EventArgs e)
        {
            AddSessionEvent(new UpdateSession(AppKey, Device.DeviceId, (int)DateTime.Now.Subtract(startTime).TotalSeconds));
        }

        /// <summary>
        /// End Countly tracking session.
        /// Call from your closing event.
        /// </summary>
        public static async Task EndSession()
        {
            if (Timer != null)
            {
                Timer.Stop();
                Timer.Tick -= UpdateSession;
                Timer = null;
            }

            await AddSessionEvent(new EndSession(AppKey, Device.DeviceId), true);
        }

        /// <summary>
        ///  Adds session event to queue and uploads
        /// </summary>
        /// <param name="sessionEvent">session event object</param>
        /// <param name="uploadImmediately">indicates when start to upload, by default - immediately after event was added</param>
        private static async Task AddSessionEvent(SessionEvent sessionEvent, bool uploadImmediately = true)
        {
            try
            {
                if (String.IsNullOrEmpty(ServerUrl))
                {
                    throw new InvalidOperationException("session is not active");
                }

                lock (sync)
                {
                    Sessions.Add(sessionEvent);

                    SaveSessions();
                }

                if (uploadImmediately)
                {
                    await Upload();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Uploads sessions queue to Countly server
        /// </summary>
        /// <returns></returns>
        private static async Task<bool> UploadSessions()
        {
            lock (sync)
            {
                if (uploadInProgress) return true;

                uploadInProgress = true;
            }

            SessionEvent sessionEvent = null;

            lock (sync)
            {
                if (Sessions.Count > 0)
                {
                    sessionEvent = Sessions[0];
                }
            }

            if (sessionEvent != null)
            {
                ResultResponse resultResponse = await Api.SendSession(ServerUrl, sessionEvent, (UserDetails.isChanged) ? UserDetails : null);

                if (resultResponse != null && resultResponse.IsSuccess)
                {
                    UserDetails.isChanged = false;

                    SaveUserDetails();

                    lock (sync)
                    {
                        uploadInProgress = false;

                        try
                        {
                            Sessions.RemoveAt(0);
                        }
                        catch { }

                        Storage.SaveToFile(sessionsFilename, Sessions);
                    }

                    if (Sessions.Count > 0)
                    {
                        return await UploadSessions();
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    uploadInProgress = false;

                    return false;
                }
            }
            else
            {
                uploadInProgress = false;

                return true;
            }
        }

        /// <summary>
        /// Immediately disables session, event, exceptions & user details tracking and clears any stored sessions, events, exceptions & user details data.
        /// This API is useful if your app has a tracking opt-out switch, and you want to immediately
        /// disable tracking when a user opts out. The EndSession/RecordEvent methods will throw
        /// InvalidOperationException after calling this until Countly is reinitialized by calling StartSession
        /// again.
        /// </summary>
        public static void Halt()
        {
            lock (sync)
            {
                ServerUrl = null;
                AppKey = null;

                if (Timer != null)
                {
                    Timer.Stop();
                    Timer.Tick -= UpdateSession;
                    Timer = null;
                }

                Events.Clear();
                Sessions.Clear();
                Exceptions.Clear();
                breadcrumb = String.Empty;
                if (userDetails != null)
                {
                    userDetails.UserDetailsChanged -= OnUserDetailsChanged;
                }
                userDetails = new CountlyUserDetails();

                Storage.DeleteFile(eventsFilename);
                Storage.DeleteFile(sessionsFilename);
                Storage.DeleteFile(exceptionsFilename);
                Storage.DeleteFile(userDetailsFilename);                
            }
        }

        /// <summary>
        /// Records a custom event with no segmentation values, a count of one and a sum of zero
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        public static void RecordEvent(string Key)
        {
            RecordCountlyEvent(Key, 1, null, null);
        }

        /// <summary>
        /// Records a custom event with no segmentation values, the specified count, and a sum of zero.
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        /// <param name="Count">Count to associate with the event, should be more than zero</param>
        public static void RecordEvent(string Key, int Count)
        {
            RecordCountlyEvent(Key, Count, null, null);
        }

        /// <summary>
        /// Records a custom event with no segmentation values, and the specified count and sum.
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        /// <param name="Count">Count to associate with the event, should be more than zero</param>
        /// <param name="Sum">Sum to associate with the event</param>
        public static void RecordEvent(string Key, int Count, double Sum)
        {
            RecordCountlyEvent(Key, Count, Sum, null);
        }

        /// <summary>
        /// Records a custom event with the specified segmentation values and count, and a sum of zero.
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        /// <param name="Count">Count to associate with the event, should be more than zero</param>
        /// <param name="Segmentation">Segmentation object to associate with the event, can be null</param>
        public static void RecordEvent(string Key, int Count, Segmentation Segmentation)
        {
            RecordCountlyEvent(Key, Count, null, Segmentation);
        }

        /// <summary>
        /// Records a custom event with the specified segmentation values, count and a sum
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        /// <param name="Count">Count to associate with the event, should be more than zero</param>
        /// <param name="Sum">Sum to associate with the event</param>
        /// <param name="Segmentation">Segmentation object to associate with the event, can be null</param>
        public static void RecordEvent(string Key, int Count, double Sum, Segmentation Segmentation)
        {
            RecordCountlyEvent(Key, Count, Sum, Segmentation);
        }

        /// <summary>
        /// Records a custom event with the specified values
        /// </summary>
        /// <param name="Key">Name of the custom event, required, must not be the empty string</param>
        /// <param name="Count">Count to associate with the event, should be more than zero</param>
        /// <param name="Sum">Sum to associate with the event</param>
        /// <param name="Segmentation">Segmentation object to associate with the event, can be null</param>
        /// <returns>True if event is uploaded successfully, False - queued for delayed upload</returns>
        private static Task<bool> RecordCountlyEvent(string Key, int Count, double? Sum, Segmentation Segmentation)
        {
            return AddEvent(new CountlyEvent(Key, Count, Sum, Segmentation));
        }

        /// <summary>
        /// Adds event to queue and uploads
        /// </summary>
        /// <param name="countlyEvent">event object</param>
        /// <returns>True if success</returns>
        private async static Task<bool> AddEvent(CountlyEvent countlyEvent)
        {
            if (String.IsNullOrEmpty(ServerUrl))
            {
                throw new InvalidOperationException("session is not active");
            }

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

            ThreadPool.QueueUserWorkItem(async (work) =>
            {
                lock (sync)
                {
                    Events.Add(countlyEvent);

                    SaveEvents();
                }

                bool success = await Upload();

                tcs.SetResult(success);
            });

            return await tcs.Task;
        }

        /// <summary>
        /// Uploads event queue to Countly server
        /// </summary>
        /// <returns>True if success</returns>
        private static async Task<bool> UploadEvents()
        {
            lock (sync)
            {
                // Allow uploading in one thread only
                if (uploadInProgress) return true;

                uploadInProgress = true;
            }

            int eventsCount;

            lock (sync)
            {
                eventsCount = Events.Count;
            }

            if (eventsCount > 0)
            {
                ResultResponse resultResponse = await Api.SendEvents(ServerUrl, AppKey, Device.DeviceId, Events.Take(eventsCount).ToList(), (UserDetails.isChanged) ? UserDetails : null);

                if (resultResponse != null && resultResponse.IsSuccess)
                {
                    int eventsCountToUploadAgain = 0;

                    UserDetails.isChanged = false;

                    SaveUserDetails();

                    lock (sync)
                    {
                        uploadInProgress = false;

                        try
                        {
                            for (int i = eventsCount - 1; i >= 0; i--)
                            {
                                Events.RemoveAt(i);
                            }
                        }
                        catch { }

                        SaveEvents();

                        eventsCountToUploadAgain = Events.Count;
                    }

                    if (eventsCountToUploadAgain > 0)
                    {
                        // Upload events added during sync
                        return await UploadEvents();
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    uploadInProgress = false;

                    return false;
                }
            }
            else
            {
                uploadInProgress = false;

                return true;
            }
        }

        /// <summary>
        /// Raised when user details propery is changed
        /// </summary>
        private static async void OnUserDetailsChanged()
        {
            UserDetails.isChanged = true;

            SaveUserDetails();

            await UploadUserDetails();
        }

        /// <summary>
        /// Uploads user details
        /// </summary>
        /// <returns>true if details are successfully uploaded, false otherwise</returns>
        internal static async Task<bool> UploadUserDetails()
        {
            if (String.IsNullOrEmpty(Countly.ServerUrl))
            {
                throw new InvalidOperationException("session is not active");
            }

            ResultResponse resultResponse = await Api.UploadUserDetails(Countly.ServerUrl, Countly.AppKey, Device.DeviceId, UserDetails);

            if (resultResponse != null && resultResponse.IsSuccess)
            {
                UserDetails.isChanged = false;

                SaveUserDetails();

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Uploads user picture. Accepted picture formats are .png, .gif and .jpeg and picture will be resized to maximal 150x150 dimensions
        /// </summary>
        /// <param name="stream">Image stream</param>
        /// <returns>true if image is successfully uploaded, false otherwise</returns>
        internal static async Task<bool> UploadUserPicture(Stream imageStream)
        {
            if (String.IsNullOrEmpty(Countly.ServerUrl))
            {
                throw new InvalidOperationException("session is not active");
            }

            ResultResponse resultResponse = await Api.UploadUserPicture(Countly.ServerUrl, Countly.AppKey, Device.DeviceId, imageStream, (UserDetails.isChanged) ? UserDetails : null);

            return (resultResponse != null && resultResponse.IsSuccess);
        }

        /// <summary>
        /// Records exception
        /// </summary>
        /// <param name="error">exception title</param>
        /// <returns>True if exception successfully uploaded, False - queued for delayed upload</returns>
        public static async Task<bool> RecordException(string error)
        {
            return await RecordException(error, null, null);
        }

        /// <summary>
        /// Records exception with stacktrace
        /// </summary>
        /// <param name="error">exception title</param>
        /// <param name="stackTrace">exception stacktrace</param>
        /// <returns>True if exception successfully uploaded, False - queued for delayed upload</returns>
        public static async Task<bool> RecordException(string error, string stackTrace)
        {
            return await RecordException(error, stackTrace, null);
        }

        /// <summary>
        /// Records exception with stacktrace and custom info
        /// </summary>
        /// <param name="error">exception title</param>
        /// <param name="stackTrace">exception stacktrace</param>
        /// <param name="customInfo">exception custom info</param>
        /// <returns>True if exception successfully uploaded, False - queued for delayed upload</returns>
        public static async Task<bool> RecordException(string error, string stackTrace, Dictionary<string, string> customInfo)
        {
            return await RecordException(error, stackTrace, customInfo, false);
        }

        /// <summary>
        /// Records exception with stacktrace and custom info
        /// </summary>
        /// <param name="error">exception title</param>
        /// <param name="stackTrace">exception stacktrace</param>
        /// <param name="customInfo">exception custom info</param>
        /// <param name="unhandled">bool indicates is exception is fatal or not</param>
        /// <returns>True if exception successfully uploaded, False - queued for delayed upload</returns>
        public static async Task<bool> RecordException(string error, string stackTrace, Dictionary<string, string> customInfo, bool unhandled)
        {
            if (String.IsNullOrEmpty(ServerUrl))
            {
                throw new InvalidOperationException("session is not active");
            }

            TimeSpan run = DateTime.Now.Subtract(startTime);

            lock (sync)
            {
                Exceptions.Add(new ExceptionEvent(error, stackTrace, unhandled, breadcrumb, run, AppVersion, customInfo));

                SaveExceptions();
            }

            if (!unhandled)
            {
                return await Upload();
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Uploads exceptions queue to Countly server
        /// </summary>
        /// <returns>True if success</returns>
        private static async Task<bool> UploadExceptions()
        {
            lock (sync)
            {
                // Allow uploading in one thread only
                if (uploadInProgress) return true;

                uploadInProgress = true;
            }

            int exceptionsCount;

            lock (sync)
            {
                exceptionsCount = Exceptions.Count;
            }

            if (exceptionsCount > 0)
            {
                ResultResponse resultResponse = await Api.SendException(ServerUrl, AppKey, Device.DeviceId, Exceptions[0]);

                if (resultResponse != null && resultResponse.IsSuccess)
                {
                    int exceptionsCountToUploadAgain = 0;

                    lock (sync)
                    {
                        uploadInProgress = false;

                        try
                        {
                            Exceptions.RemoveAt(0);
                        }
                        catch { }

                        SaveExceptions();

                        exceptionsCountToUploadAgain = Exceptions.Count;
                    }

                    if (exceptionsCountToUploadAgain > 0)
                    {
                        // Upload next exception
                        return await UploadExceptions();
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    uploadInProgress = false;

                    return false;
                }
            }
            else
            {
                uploadInProgress = false;

                return false;
            }
        }

        /// <summary>
        /// Adds log breadcrumb
        /// </summary>
        /// <param name="log">log string</param>
        public static void AddBreadCrumb(string log)
        {
            breadcrumb += log + "\r\n";
        }

        /// <summary>
        /// Upload sessions, events & exception queues
        /// </summary>
        /// <returns>True if success</returns>
        private static async Task<bool> Upload()
        {
            bool success = await UploadSessions();

            if (success)
            {
                success = await UploadEvents();
            }

            if (success)
            {
                success = await UploadExceptions();
            }

            return success;
        }
    }
}
