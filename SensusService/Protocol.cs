// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Newtonsoft.Json;
using SensusService.DataStores.Local;
using SensusService.DataStores.Remote;
using SensusService.Probes;
using SensusUI.UiProperties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Xamarin.Forms;
using SensusService.Anonymization;
using System.Linq;
using System.Reflection;
using SensusUI;
using SensusService.Probes.Location;
using SensusService.Exceptions;
using SensusUI.Inputs;
using SensusService.Probes.User;
using SensusService.Probes.Apps;

#if __IOS__
using HealthKit;
using Sensus.iOS.Probes.User.Health;
using Foundation;
#endif

namespace SensusService
{
    /// <summary>
    /// Container for probes, data stores, and all other information needed to run a collection experiment.
    /// </summary>
    public class Protocol
    {
        #region static members

        public static void CreateAsync(string name, Action<Protocol> callback)
        {
            Probe.GetAllAsync(probes =>
                {
                    Protocol protocol = new Protocol(name);

                    foreach (Probe probe in probes)
                        protocol.AddProbe(probe);

                    SensusServiceHelper.Get().RegisterProtocol(protocol);

                    if (callback != null)
                        callback(protocol);
                });
        }

        public static void DeserializeAsync(Uri webURI, Action<List<Protocol>> callback)
        {
            try
            {
                WebClient downloadClient = new WebClient();

                #if __ANDROID__ || __IOS__
                downloadClient.DownloadDataCompleted += (o, e) =>
                {
                    DeserializeAsync(e.Result, callback);
                };
                #elif WINDOWS_PHONE
                // TODO:  Read bytes and display.
                #else
                #error "Unrecognized platform."
                #endif

                downloadClient.DownloadDataAsync(webURI);
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Failed to download protocols from URI \"" + webURI + "\":  " + ex.Message + ". If this is an HTTPS URI, make sure the server's certificate is valid.", LoggingLevel.Normal, typeof(Protocol));
                SensusServiceHelper.Get().FlashNotificationAsync("Failed to download protocols.");
            }
        }

        public static void DeserializeAsync(byte[] bytes, Action<List<Protocol>> callback)
        {
            try
            {
                DeserializeAsync(SensusServiceHelper.Decrypt(bytes), callback);
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Failed to decrypt protocols from bytes:  " + ex.Message, LoggingLevel.Normal, typeof(Protocol));
                SensusServiceHelper.Get().FlashNotificationAsync("Failed to decrypt protocols.");
            }                        
        }

        public static void DeserializeAsync(string json, Action<List<Protocol>> callback)
        {
            new Thread(() =>
                {
                    List<Protocol> protocols = null;

                    try
                    {
                        #region allow protocols to be opened across platforms by modifying the namespaces in the JSON
                        string newJSON;
                        string typeName = SensusServiceHelper.Get().GetType().Name;
                        if (typeName == "AndroidSensusServiceHelper")
                            newJSON = json.Replace("iOS", "Android").Replace("WinPhone", "Android");
                        else if (typeName == "iOSSensusServiceHelper")
                            newJSON = json.Replace("Android", "iOS").Replace("WinPhone", "iOS");
                        else if (typeName == "WinPhone")
                            newJSON = json.Replace("Android", "WinPhone").Replace("iOS", "WinPhone");
                        else
                            throw new SensusException("Attempted to deserialize JSON into unknown service helper type:  " + SensusServiceHelper.Get().GetType().FullName);

                        if (newJSON == json)
                            SensusServiceHelper.Get().Logger.Log("No cross-platform conversion required for service helper JSON.", LoggingLevel.Normal, typeof(Protocol));
                        else
                        {
                            SensusServiceHelper.Get().Logger.Log("Performed cross-platform conversion of service helper JSON.", LoggingLevel.Normal, typeof(Protocol));
                            json = newJSON;
                        }
                        #endregion

                        ManualResetEvent protocolsWait = new ManualResetEvent(false);

                        // always deserialize protocols on the main thread (e.g., since a looper is required for android)
                        Device.BeginInvokeOnMainThread(() =>
                            {
                                try
                                {
                                    protocols = JsonConvert.DeserializeObject<List<Protocol>>(json, SensusServiceHelper.JSON_SERIALIZER_SETTINGS);
                                }
                                catch (Exception ex)
                                {
                                    SensusServiceHelper.Get().Logger.Log("Error while deserializing protocols from JSON:  " + ex.Message, LoggingLevel.Normal, typeof(Protocol));
                                }
                                finally
                                {
                                    protocolsWait.Set();
                                }                              
                            });

                        protocolsWait.WaitOne();

                        if (protocols == null)
                        {
                            SensusServiceHelper.Get().Logger.Log("Failed to deserialize protocols from JSON.", LoggingLevel.Normal, typeof(Protocol));
                            SensusServiceHelper.Get().FlashNotificationAsync("Failed to deserialize protocols from JSON.");
                        }
                        else
                        {     
                            for (int i = 0; i < protocols.Count; ++i)
                            {
                                Protocol protocol = protocols[i];

                                // see if we have already registered the protocol
                                Protocol registeredProtocol = SensusServiceHelper.Get().RegisteredProtocols.FirstOrDefault(p => p.Id == protocol.Id);

                                // if we haven't registered the protocol, then set it up and register it
                                if (registeredProtocol == null)
                                {
                                    ManualResetEvent setupWait = new ManualResetEvent(false);

                                    Probe.GetAllAsync(probes =>
                                        {
                                            // add any probes for the current platform that didn't come through when deserializing. for example, android has a listening WLAN probe, but iOS has a polling WLAN probe. neither will come through on the other platform when deserializing, since the types are not defined.
                                            List<Type> deserializedProbeTypes = protocol.Probes.Select(p => p.GetType()).ToList();

                                            foreach (Probe probe in probes)
                                                if (!deserializedProbeTypes.Contains(probe.GetType()))
                                                {
                                                    SensusServiceHelper.Get().Logger.Log("Adding missing probe to protocol:  " + probe.GetType().FullName, LoggingLevel.Normal, typeof(Protocol));
                                                    protocol.AddProbe(probe);
                                                }     

                                            // reset the random time anchor -- we shouldn't use the same one that someone else used
                                            protocol.ResetRandomTimeAnchor();

                                            // reset the storage directory
                                            protocol.StorageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), protocol.Id);
                                            if (!Directory.Exists(protocol.StorageDirectory))
                                                Directory.CreateDirectory(protocol.StorageDirectory);                                                       

                                            SensusServiceHelper.Get().RegisterProtocol(protocol);

                                            setupWait.Set();
                                        });

                                    setupWait.WaitOne();
                                }
                                else
                                    protocols[i] = registeredProtocol;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to deserialize protocols from JSON:  " + ex.Message, LoggingLevel.Normal, typeof(Protocol));
                        SensusServiceHelper.Get().FlashNotificationAsync("Failed to deserialize protocols from JSON:  " + ex.Message);
                    }

                    if (callback != null)
                        callback(protocols);

                }).Start();
        }

        public static void DisplayAndStartAsync(List<Protocol> protocols)
        {
            new Thread(() =>
                {
                    // display the protocols page if it isn't already up
                    Device.BeginInvokeOnMainThread(async () =>
                        {
                            if (!(App.Current.MainPage.Navigation.NavigationStack.Last() is ProtocolsPage))
                                await App.Current.MainPage.Navigation.PushAsync(new ProtocolsPage());
                        });

                    // offer to start each protocol
                    foreach (Protocol protocol in protocols)
                    {
                        if (protocol == null)
                            SensusServiceHelper.Get().FlashNotificationAsync("Protocol is empty. Cannot display or start it.");
                        else if (protocol.Running)
                            SensusServiceHelper.Get().FlashNotificationAsync("You are already participating in \"" + protocol.Name + "\".");
                        else
                        {
                            ManualResetEvent startWait = new ManualResetEvent(false);
                            protocol.StartWithUserAgreementAsync("You just opened \"" + protocol.Name + "\" within Sensus." + (string.IsNullOrWhiteSpace(protocol.StartupAgreement) ? "" : " Please read the following terms and conditions."), () =>
                                {
                                    startWait.Set();
                                });

                            startWait.WaitOne();
                        }
                    }

                }).Start();
        }

        public static void RunUnitTestingProtocol(Stream protocolFile)
        {
            try
            {
                if (SensusServiceHelper.Get().RegisteredProtocols.Count == 0)
                {
                    using (MemoryStream protocolStream = new MemoryStream())
                    {
                        protocolFile.CopyTo(protocolStream);
                        string protocolJSON = SensusServiceHelper.Decrypt(protocolStream.ToArray());
                        DeserializeAsync(protocolJSON, protocols =>
                            {
                                if (protocols == null)
                                    throw new Exception("Failed to deserialize unit testing protocol.");
                                else if (protocols.Count != 1)
                                    throw new Exception("Unit testing protocol file contained " + protocols.Count + " protocols. Expected 1.");

                                Protocol protocol = protocols[0];

                                // unit testing is problematic with probes that take us away from Sensus, since it's difficult to automate UI 
                                // interaction outside of Sensus. disable any probes that might take us away from Sensus.
                                foreach (Probe probe in protocol.Probes)
                                {
                                    if (probe is FacebookProbe)
                                        probe.Enabled = false;

                                    #if __IOS__
                                    if (probe is iOSHealthKitProbe)
                                        probe.Enabled = false;
                                    #endif
                                }

                                DisplayAndStartAsync(protocols);
                            });
                    }
                }
            }
            catch (Exception ex)
            {
                string message = "Failed to open unit testing protocol:  " + ex.Message;
                SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, typeof(Protocol));
                throw new Exception(message);
            }
        }

        #endregion

        public event EventHandler<bool> ProtocolRunningChanged;

        private string _id;
        private string _name;
        private List<Probe> _probes;
        private bool _running;
        private LocalDataStore _localDataStore;
        private RemoteDataStore _remoteDataStore;
        private string _storageDirectory;
        private ProtocolReport _mostRecentReport;
        private bool _forceProtocolReportsToRemoteDataStore;
        private string _lockPasswordHash;
        private AnonymizedJsonContractResolver _jsonAnonymizer;
        private DateTimeOffset _randomTimeAnchor;
        private bool _shareable;
        private List<PointOfInterest> _pointsOfInterest;
        private string _startupAgreement;
        private int _participationHorizonDays;
        private string _contactEmail;
        private bool _groupable;
        private List<Protocol> _groupedProtocols;

        private readonly object _locker = new object();

        public string Id
        {
            get { return _id; }
            set { _id = value; }
        }

        [EntryStringUiProperty("Name:", true, 1)]
        public string Name
        {
            get { return _name; }
            set { _name = value; }
        }

        public List<Probe> Probes
        {
            get { return _probes; }
            set { _probes = value; }
        }

        [JsonIgnore]
        public bool Running
        {
            get { return _running; }
            set
            {
                if (value != _running)
                {
                    if (value)
                        StartAsync();
                    else
                        StopAsync();
                }
            }
        }

        public LocalDataStore LocalDataStore
        {
            get { return _localDataStore; }
            set
            {
                if (value != _localDataStore)
                {
                    _localDataStore = value;
                    _localDataStore.Protocol = this;
                }
            }
        }

        public RemoteDataStore RemoteDataStore
        {
            get { return _remoteDataStore; }
            set
            {
                if (value != _remoteDataStore)
                {
                    _remoteDataStore = value;
                    _remoteDataStore.Protocol = this;
                }
            }
        }

        public string StorageDirectory
        {
            get { return _storageDirectory; }
            set { _storageDirectory = value; }
        }

        [JsonIgnore]
        public ProtocolReport MostRecentReport
        {
            get { return _mostRecentReport; }
            set { _mostRecentReport = value; }
        }

        [OnOffUiProperty("Force Reports to Remote:", true, 20)]
        public bool ForceProtocolReportsToRemoteDataStore
        {
            get { return _forceProtocolReportsToRemoteDataStore; }
            set { _forceProtocolReportsToRemoteDataStore = value; }
        }

        public string LockPasswordHash
        {
            get
            {
                return _lockPasswordHash; 
            }
            set
            {
                _lockPasswordHash = value;
            }
        }

        public AnonymizedJsonContractResolver JsonAnonymizer
        {
            get { return _jsonAnonymizer; }
            set { _jsonAnonymizer = value; }
        }

        public DateTimeOffset RandomTimeAnchor
        {
            get
            {
                return _randomTimeAnchor;
            }
            set
            {
                _randomTimeAnchor = value;
            }
        }

        [OnOffUiProperty("Shareable:", true, 10)]
        public bool Shareable
        {
            get
            {
                return _shareable;
            }
            set
            {
                _shareable = value;
            }
        }

        public List<PointOfInterest> PointsOfInterest
        {
            get { return _pointsOfInterest; }
        }

        [EditorUiProperty("Startup Agreement:", true, 15)]
        public string StartupAgreement
        {
            get
            {
                return _startupAgreement;
            }
            set
            {
                _startupAgreement = value;
            }
        }

        [EntryIntegerUiProperty("Participation Horizon (Days):", true, 16)]
        public int ParticipationHorizonDays
        {
            get
            {
                return _participationHorizonDays;
            }
            set
            {
                _participationHorizonDays = value;
            }
        }

        [JsonIgnore]
        public DateTime ParticipationHorizon
        {
            get { return DateTime.Now.AddDays(-_participationHorizonDays); }
        }

        [EntryStringUiProperty("Contact Email:", true, 18)]
        public string ContactEmail
        {
            get
            {
                return _contactEmail;
            }
            set
            {
                _contactEmail = value;
            }
        }

        [OnOffUiProperty(null, true, 19)]
        public bool Groupable
        {
            get
            {
                return _groupable;
            }
            set
            {
                _groupable = value;
            }
        }

        public List<Protocol> GroupedProtocols
        {
            get
            {
                return _groupedProtocols;
            }
            set
            {
                _groupedProtocols = value;
            }
        }

        [JsonIgnore]
        public float Participation
        {
            get
            { 
                float[] participations = _probes.Select(probe => probe.GetParticipation())
                                                .Where(participation => participation != null)
                                                .Select(participation => participation.GetValueOrDefault())
                                                .ToArray();

                // there will not be any participations if all probes are disabled -- perfect participation by definition
                if (participations.Length == 0)
                    return 1;
                else
                    return participations.Average();
            }
        }

        /// <summary>
        /// For JSON deserialization
        /// </summary>
        private Protocol()
        {
            _running = false;
            _forceProtocolReportsToRemoteDataStore = false;
            _lockPasswordHash = "";
            _jsonAnonymizer = new AnonymizedJsonContractResolver(this);
            _shareable = false;
            _pointsOfInterest = new List<PointOfInterest>();
            _participationHorizonDays = 1;   
            _groupable = false;
            _groupedProtocols = new List<Protocol>();
        }

        /// <summary>
        /// Called by static CreateAsync. Should not be called directly by outside callers.
        /// </summary>
        /// <param name="name">Name.</param>
        private Protocol(string name)
            : this()
        {
            _name = name;

            ResetRandomTimeAnchor();            

            while (_storageDirectory == null)
            {
                _id = Guid.NewGuid().ToString();
                string candidateStorageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), _id);
                if (!Directory.Exists(candidateStorageDirectory))
                {
                    _storageDirectory = candidateStorageDirectory;
                    Directory.CreateDirectory(_storageDirectory);
                }
            }

            _probes = new List<Probe>();
        }

        private void AddProbe(Probe probe)
        {
            probe.Protocol = this;

            // since the new probe was just bound to this protocol, we need to let this protocol know about this probe's default anonymization preferences.
            foreach (PropertyInfo anonymizableProperty in probe.DatumType.GetProperties().Where(property => property.GetCustomAttribute<Anonymizable>() != null))
            {
                Anonymizable anonymizableAttribute = anonymizableProperty.GetCustomAttribute<Anonymizable>(true);
                _jsonAnonymizer.SetAnonymizer(anonymizableProperty, anonymizableAttribute.DefaultAnonymizer);
            }

            _probes.Add(probe);
        }

        private void ResetRandomTimeAnchor()
        {
            // pick a random time within the first 1000 years AD.
            _randomTimeAnchor = new DateTimeOffset((long)(new Random().NextDouble() * new DateTimeOffset(1000, 1, 1, 0, 0, 0, new TimeSpan()).Ticks), new TimeSpan());
        }

        public void CopyAsync(Action<Protocol> callback)
        {
            new Thread(() =>
                {
                    Device.BeginInvokeOnMainThread(() =>
                        {
                            callback(JsonConvert.DeserializeObject<Protocol>(JsonConvert.SerializeObject(this, SensusServiceHelper.JSON_SERIALIZER_SETTINGS), SensusServiceHelper.JSON_SERIALIZER_SETTINGS));
                        });

                }).Start();
        }

        public void StartAsync()
        {
            new Thread(Start).Start();
        }

        public void Start()
        {
            lock (_locker)
            {
                if (_running)
                    return;
                else
                    _running = true;

                if (ProtocolRunningChanged != null)
                    ProtocolRunningChanged(this, _running);

                SensusServiceHelper.Get().AddRunningProtocolId(_id);

                bool stopProtocol = false;

                // start local data store
                try
                {
                    if (_localDataStore == null)
                        throw new Exception("Local data store not defined.");
                        
                    _localDataStore.Start();

                    // start remote data store
                    try
                    {
                        if (_remoteDataStore == null)
                            throw new Exception("Remote data store not defined.");
                            
                        _remoteDataStore.Start();

                        // start probes
                        try
                        {
                            // if we're on iOS, gather up all of the health-kit probes so that we can request their permissions in one batch
                            #if __IOS__
                            if (HKHealthStore.IsHealthDataAvailable)
                            {
                                List<iOSHealthKitProbe> enabledHealthKitProbes = new List<iOSHealthKitProbe>();
                                foreach (Probe probe in _probes)
                                    if (probe.Enabled && probe is iOSHealthKitProbe)
                                        enabledHealthKitProbes.Add(probe as iOSHealthKitProbe);                                                                            

                                if (enabledHealthKitProbes.Count > 0)
                                {
                                    NSSet objectTypesToRead = NSSet.MakeNSObjectSet<HKObjectType>(enabledHealthKitProbes.Select(probe => probe.ObjectType).Distinct().ToArray());
                                    ManualResetEvent authorizationWait = new ManualResetEvent(false);
                                    new HKHealthStore().RequestAuthorizationToShare(new NSSet(), objectTypesToRead,
                                        (success, error) =>
                                        {
                                            if (error != null)
                                                SensusServiceHelper.Get().Logger.Log("Error while requesting HealthKit authorization:  " + error.Description, LoggingLevel.Normal, GetType());

                                            authorizationWait.Set();
                                        });

                                    authorizationWait.WaitOne();
                                }
                            }
                            #endif

                            SensusServiceHelper.Get().Logger.Log("Starting probes for protocol " + _name + ".", LoggingLevel.Normal, GetType());
                            int probesEnabled = 0;
                            int probesStarted = 0;
                            foreach (Probe probe in _probes)
                                if (probe.Enabled)
                                {
                                    ++probesEnabled;

                                    try
                                    {
                                        probe.Start();
                                        probesStarted++;
                                    }
                                    catch (Exception ex)
                                    {
                                        // stop probe to clean up any inconsistent state information
                                        try
                                        {
                                            probe.Stop();
                                        }
                                        catch (Exception ex2)
                                        {
                                            SensusServiceHelper.Get().Logger.Log("Failed to stop probe after failing to start it:  " + ex2.Message, LoggingLevel.Normal, GetType());
                                        }

                                        string message = "Failed to start probe \"" + probe.GetType().FullName + "\":  " + ex.Message;
                                        SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                                        SensusServiceHelper.Get().FlashNotificationAsync(message);

                                        // disable probe if it is not supported on the device (or if the user has elected not to enable it -- e.g., by refusing to log into facebook)
                                        if (ex is NotSupportedException)
                                            probe.Enabled = false;
                                    }
                                }

                            if (probesEnabled == 0)
                                throw new Exception("No probes were enabled.");
                            else if (probesStarted == 0)
                                throw new Exception("No probes started.");
                            else
                                SensusServiceHelper.Get().FlashNotificationAsync("Started \"" + _name + "\".");
                        }
                        catch (Exception ex)
                        {
                            string message = "Failure while starting probes:  " + ex.Message;
                            SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                            SensusServiceHelper.Get().FlashNotificationAsync(message);
                            stopProtocol = true;
                        }                            
                    }
                    catch (Exception ex)
                    {
                        string message = "Remote data store failed to start:  " + ex.Message;
                        SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                        SensusServiceHelper.Get().FlashNotificationAsync(message);
                        stopProtocol = true;
                    }
                }
                catch (Exception ex)
                {
                    string message = "Local data store failed to start:  " + ex.Message;
                    SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                    SensusServiceHelper.Get().FlashNotificationAsync(message);
                    stopProtocol = true;
                }

                if (stopProtocol)
                    Stop();
            }
        }

        public void StartWithUserAgreementAsync(string message, Action callback = null)
        {
            int consentCode = new Random().Next(1000, 10000);

            SensusServiceHelper.Get().PromptForInputsAsync(

                "Protocol Consent", 

                new Input[]
                {
                    new LabelOnlyInput(
                        "ConsentMessage",
                        (string.IsNullOrWhiteSpace(message) ? "" : message + Environment.NewLine + Environment.NewLine) +
                        (string.IsNullOrWhiteSpace(_startupAgreement) ? "" : _startupAgreement + Environment.NewLine + Environment.NewLine) +
                        "To participate in this study, please indicate your consent by entering the following code:  " + consentCode),

                    new TextInput("ConsentCode", null)
                },

                null,

                inputs =>
                {
                    if (inputs != null)
                    {
                        string consentCodeStr = inputs[1].Value as string;

                        int consentCodeInt;
                        if (int.TryParse(consentCodeStr, out consentCodeInt) && consentCodeInt == consentCode)
                            Running = true;
                        else
                            SensusServiceHelper.Get().FlashNotificationAsync("Incorrect code entered.");
                    }

                    if (callback != null)
                        callback();
                });
        }

        public void TestHealthAsync(bool userInitiated)
        {
            TestHealthAsync(userInitiated, () =>
                {
                });
        }

        public void TestHealthAsync(bool userInitiated, Action callback)
        {
            new Thread(() =>
                {
                    TestHealth(userInitiated);

                    if (callback != null)
                        callback();

                }).Start();
        }

        public void TestHealth(bool userInitiated)
        {
            lock (_locker)
            {
                string error = null;
                string warning = null;
                string misc = null;

                if (!_running)
                {
                    error += "Restarting protocol \"" + _name + "\"...";
                    try
                    {
                        Stop();
                        Start();
                    }
                    catch (Exception ex)
                    {
                        error += ex.Message + "...";
                    }

                    if (_running)
                        error += "restarted protocol." + Environment.NewLine;
                    else
                        error += "failed to restart protocol." + Environment.NewLine;
                }

                if (_running)
                {
                    if (_localDataStore == null)
                        error += "No local data store present on protocol." + Environment.NewLine;
                    else if (_localDataStore.TestHealth(ref error, ref warning, ref misc))
                    {
                        error += "Restarting local data store...";

                        try
                        {
                            _localDataStore.Restart();
                        }
                        catch (Exception ex)
                        {
                            error += ex.Message + "...";
                        }

                        if (!_localDataStore.Running)
                            error += "failed to restart local data store." + Environment.NewLine;
                    }

                    if (_remoteDataStore == null)
                        error += "No remote data store present on protocol." + Environment.NewLine;
                    else if (_remoteDataStore.TestHealth(ref error, ref warning, ref misc))
                    {
                        error += "Restarting remote data store...";

                        try
                        {
                            _remoteDataStore.Restart();
                        }
                        catch (Exception ex)
                        {
                            error += ex.Message + "...";
                        }

                        if (!_remoteDataStore.Running)
                            error += "failed to restart remote data store." + Environment.NewLine;
                    }

                    foreach (Probe probe in _probes)
                        if (probe.Enabled)
                        {
                            if (probe.TestHealth(ref error, ref warning, ref misc))
                            {
                                error += "Restarting probe \"" + probe.GetType().FullName + "\"...";

                                try
                                {
                                    probe.Restart();
                                }
                                catch (Exception ex)
                                {
                                    error += ex.Message + "...";
                                }

                                if (!probe.Running)
                                    error += "failed to restart probe \"" + probe.GetType().FullName + "\"." + Environment.NewLine;
                            }
                            else
                            {
                                // keep track of successful system-initiated health tests. this tells use how consistently the probe is running.
                                if (!userInitiated)
                                    lock (probe.SuccessfulHealthTestTimes)
                                    {
                                        probe.SuccessfulHealthTestTimes.Add(DateTime.Now);
                                        probe.SuccessfulHealthTestTimes.RemoveAll(healthTestTime => healthTestTime < ParticipationHorizon);
                                    }
                            }
                        }
                }

                _mostRecentReport = new ProtocolReport(DateTimeOffset.UtcNow, error, warning, misc);
                SensusServiceHelper.Get().Logger.Log("Protocol report:" + Environment.NewLine + _mostRecentReport, LoggingLevel.Normal, GetType());

                SensusServiceHelper.Get().Logger.Log("Storing protocol report locally.", LoggingLevel.Normal, GetType());
                _localDataStore.AddNonProbeDatum(_mostRecentReport);

                if (!_localDataStore.UploadToRemoteDataStore && _forceProtocolReportsToRemoteDataStore)
                {
                    SensusServiceHelper.Get().Logger.Log("Local data aren't pushed to remote, so we're copying the report datum directly to the remote cache.", LoggingLevel.Normal, GetType());
                    _remoteDataStore.AddNonProbeDatum(_mostRecentReport);
                }

                int runningProtocols = SensusServiceHelper.Get().RunningProtocolIds.Count;
                SensusServiceHelper.Get().UpdateApplicationStatus(runningProtocols + " protocol" + (runningProtocols == 1 ? " is " : "s are") + " running");
            }
        }

        public void ResetForSharing()
        {
            _randomTimeAnchor = DateTime.MinValue;
            _storageDirectory = null;
            _mostRecentReport = null;            

            foreach (Probe probe in _probes)
            {
                probe.ResetForSharing();

                // reset enabled status of probes to the original values. probes can be disabled when the protocol is started (e.g., if the user cancels out of facebook login.)
                probe.Enabled = probe.OriginallyEnabled;
            }

            if (_localDataStore != null)
                _localDataStore.ClearForSharing();

            if (_remoteDataStore != null)
                _remoteDataStore.ClearForSharing();
        }

        public void StopAsync()
        {
            StopAsync(() =>
                {
                });
        }

        public void StopAsync(Action callback)
        {
            new Thread(() =>
                {
                    Stop();

                    if (callback != null)
                        callback();

                }).Start();
        }

        public void Stop()
        {
            lock (_locker)
            {
                if (_running)
                    _running = false;
                else
                    return;

                if (ProtocolRunningChanged != null)
                    ProtocolRunningChanged(this, _running);

                SensusServiceHelper.Get().RemoveRunningProtocolId(_id);

                SensusServiceHelper.Get().Logger.Log("Stopping protocol \"" + _name + "\".", LoggingLevel.Normal, GetType());

                foreach (Probe probe in _probes)
                    if (probe.Running)
                    {
                        try
                        {
                            probe.Stop(); 
                        }
                        catch (Exception ex)
                        {
                            SensusServiceHelper.Get().Logger.Log("Failed to stop " + probe.GetType().FullName + ":  " + ex.Message, LoggingLevel.Normal, GetType());
                        }
                    }

                if (_localDataStore != null && _localDataStore.Running)
                {
                    try
                    {
                        _localDataStore.Stop();
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to stop local data store:  " + ex.Message, LoggingLevel.Normal, GetType());
                    }
                }

                if (_remoteDataStore != null && _remoteDataStore.Running)
                {
                    try
                    {
                        _remoteDataStore.Stop();
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to stop remote data store:  " + ex.Message, LoggingLevel.Normal, GetType());
                    }
                }

                SensusServiceHelper.Get().Logger.Log("Stopped protocol \"" + _name + "\".", LoggingLevel.Normal, GetType());
                SensusServiceHelper.Get().FlashNotificationAsync("Stopped \"" + _name + "\".");
            }
        }

        public override bool Equals(object obj)
        {
            return obj is Protocol && (obj as Protocol)._id == _id;
        }

        public override int GetHashCode()
        {
            return _id.GetHashCode();
        }
    }
}
