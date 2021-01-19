using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.ClickHouse;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.ElasticSearch;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class ExportersManager : BackgroundService
    {
        private readonly ILogger<ExportersManager> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly Dictionary<string, CancellationTokenSource> _runExporters = new();
        private ClstWatcher _clstWatcher;
        // Common settings
        private readonly StorageType _storageType;
        private readonly string _clstFolder;
        private readonly List<TemplateItem> _templates;
        private readonly int _portion;
        private readonly DateTimeZone _timeZone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        private readonly int _writingMaxDop;
        private readonly int _collectedFactor;
        private readonly bool _loadArchive;
        private readonly int _readingTimeout;
        // ClickHouse
        private readonly string _connectionString;
        // ELK
        private readonly List<ElasticSearchNode> _nodes;
        private readonly string _separation;
        private readonly int _maximumRetries;
        private readonly TimeSpan _maxRetryTimeout;

        public ExportersManager(ILogger<ExportersManager> logger, IServiceProvider serviceProvider, IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            _clstFolder = configuration.GetValue("Manager:ClstFolder", "");
            _templates = configuration.GetSection("Manager:Templates").Get<List<TemplateItem>>();
            _storageType = configuration.GetValue("Exporter:StorageType", StorageType.None);
            _portion = configuration.GetValue("Exporter:Portion", 10000);
            _writingMaxDop = configuration.GetValue("Exporter:WritingMaxDegreeOfParallelism", 1);
            _collectedFactor = configuration.GetValue("Exporter:CollectedFactor", 2);
            _loadArchive = configuration.GetValue("Exporter:LoadArchive", false);
            _readingTimeout = configuration.GetValue("Exporter:ReadingTimeout", 1);

            var timeZone = configuration.GetValue("Exporter:TimeZone", "");

            if (!string.IsNullOrWhiteSpace(timeZone))
            {
                _timeZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? throw new Exception($"\"{timeZone}\" is unknown time zone");
            }

            CheckSettings();

            if (_storageType == StorageType.ClickHouse)
            {
                _connectionString = configuration.GetValue("ClickHouse:ConnectionString", "");
                if (_connectionString == string.Empty)
                    throw new Exception("Connection string is not specified");
            }
            else if (_storageType == StorageType.ElasticSearch)
            {
                _nodes = configuration.GetSection("ElasticSearch:Nodes").Get<List<ElasticSearchNode>>();
                _separation = configuration.GetValue("ElasticSearch:Separation", "H");
                _maximumRetries = configuration.GetValue("ElasticSearch:MaximumRetries", ElasticSearchStorage.DefaultMaximumRetries);
                _maxRetryTimeout = TimeSpan.FromSeconds(configuration.GetValue("ElasticSearch:MaxRetryTimeout", ElasticSearchStorage.DefaultMaxRetryTimeoutSec));
            }
        }

        private void CheckSettings()
        {
            if (_storageType == StorageType.None)
                throw new Exception("StorageType parameter is not specified");

            if (string.IsNullOrEmpty(_clstFolder))
                throw new Exception("\"ClstFolder\" property is not specified");

            if (!Directory.Exists(_clstFolder))
                throw new Exception($"Clst folder ({_clstFolder}) doesn't exist");

            if (_writingMaxDop <= 0)
                throw new Exception("WritingMaxDegreeOfParallelism cannot be equal to or less than 0");

            if (_collectedFactor <= 0)
                throw new Exception("CollectedFactor cannot be equal to or less than 0");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                lock (_runExporters)
                {
                    foreach (var ib in _runExporters)
                        ib.Value.Cancel();
                }
            });

            _clstWatcher = new ClstWatcher(_clstFolder, _templates);
            
            foreach (var (key, (name, dataBaseName)) in _clstWatcher.InfoBases)
                StartExporter(key, name, dataBaseName);

            _clstWatcher.InfoBasesAdded += ClstWatcher_InfoBasesAdded;
            _clstWatcher.InfoBasesDeleted += ClstWatcher_InfoBasesDeleted;

            await Task.Factory.StartNew(stoppingToken.WaitHandle.WaitOne, stoppingToken);
        }

        private void ClstWatcher_InfoBasesDeleted(object sender, ClstEventArgs args)
            => StartExporter(args.Id, args.Name, args.DataBaseName);

        private void ClstWatcher_InfoBasesAdded(object sender, ClstEventArgs args)
            => StopExporter(args.Id, args.Name);

        private void StartExporter(string id, string name, string dataBaseName)
        {
            var logFolder = Path.Combine(_clstFolder, id);
            logFolder = Path.Combine(logFolder, "1Cv8Log");

            // Check this is an old event log format
            var lgfPath = Path.Combine(logFolder, "1Cv8.lgf");

            var needStart = File.Exists(lgfPath);

            if (needStart)
            {
                lock (_runExporters)
                    if (!_runExporters.ContainsKey(id))
                    {
                        var cts = new CancellationTokenSource();
                        var logger = (ILogger<EventLogExporter>)_serviceProvider.GetService(typeof(ILogger<EventLogExporter>));
                        var storage = GetStorage(dataBaseName);

                        var settings = new EventLogExporterSettings
                        {
                            LogFolder = logFolder,
                            CollectedFactor = _collectedFactor,
                            LoadArchive = _loadArchive,
                            Portion = _portion,
                            ReadingTimeout = _readingTimeout,
                            TimeZone = _timeZone,
                            WritingMaxDop = _writingMaxDop
                        };

                        var exporter = new EventLogExporter(settings, storage, logger);

                        Task.Factory.StartNew(async () => {
                            try
                            {
                                await exporter.StartAsync(cts.Token);
                            }
                            catch (TaskCanceledException) { }
                            catch (Exception ex)
                            {
                                _logger?.LogCritical(ex, "Failed to execute EventLogExporter");
                            }
                        }, cts.Token);

                        _runExporters.Add(id, cts);

                        _logger?.LogInformation($"Event log exporter for \"{name}\" information base to \"{dataBaseName}\" is started");
                    }
            }
            else
                _logger?.LogInformation($"Event log of \"{name}\" information base is in \"new\" format, it won't be handled");
        }

        private void StopExporter(string id, string name)
        {
            lock(_runExporters)
                if (_runExporters.TryGetValue(id, out var cts))
                {
                    cts.Cancel();
                    _logger?.LogInformation($"Event log exporter for \"{name}\" information base is stopped");
                }
        }

        private IEventLogStorage GetStorage(string dataBaseName)
        {
            if (_storageType == StorageType.ClickHouse)
            {
                var logger = (ILogger<ClickHouseStorage>)_serviceProvider.GetService(typeof(ILogger<ClickHouseStorage>));
                var connectionString = $"{_connectionString}Database={dataBaseName};";

                return new ClickHouseStorage(connectionString, logger);
            }
            else if (_storageType == StorageType.ElasticSearch)
            {
                var logger = (ILogger<ElasticSearchStorage>)_serviceProvider.GetService(typeof(ILogger<ElasticSearchStorage>));

                var settings = new ElasticSearchStorageSettings
                {
                    Index = dataBaseName,
                    Separation = _separation,
                    MaximumRetries = _maximumRetries,
                    MaxRetryTimeout = _maxRetryTimeout
                };
                settings.Nodes.AddRange(_nodes);

                return new ElasticSearchStorage(settings, logger);
            }
            else
                throw new Exception("Try to get a storage for unknown StorageType value");
        }
    }
}
