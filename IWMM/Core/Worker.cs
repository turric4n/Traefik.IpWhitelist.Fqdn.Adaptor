﻿using FluentScheduler;
using IWMM.Entities;
using IWMM.Services.Abstractions;
using IWMM.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Entry = IWMM.Entities.Entry;

namespace IWMM.Core
{
    public class Worker : IHostedService
    {
        private readonly IHostEnvironment _hostEnvironment;
        private readonly IOptionsSnapshot<MainSettings> _optionsSnapshot;
        private readonly ILogger<Worker> _logger;
        private readonly IFqdnResolver _fqdnResolver;
        private readonly Func<SchemaType, ISchemaRepository> _schemaRepositoryLocator;
        private readonly Func<SchemaType, IEntriesToSchemaAdaptor> _schemaAdaptorLocator;
        private readonly IEntryRepository _entryRepository;
        private bool _working;

        public Worker(
            IHostEnvironment hostEnvironment,
            IOptionsSnapshot<MainSettings> optionsSnapshot, 
            ILogger<Worker> logger, 
            IFqdnResolver fqdnResolver,
            Func<SchemaType, ISchemaRepository> schemaRepositoryLocator,
            Func<SchemaType, IEntriesToSchemaAdaptor> schemaAdaptorLocator,
            IEntryRepository entryRepository)
        {
            _hostEnvironment = hostEnvironment;
            _optionsSnapshot = optionsSnapshot;
            _logger = logger;
            _fqdnResolver = fqdnResolver;
            _schemaRepositoryLocator = schemaRepositoryLocator;
            _schemaAdaptorLocator = schemaAdaptorLocator;
            _entryRepository = entryRepository;
        }

        private ISchemaRepository GetSchemaRepository(SchemaType schema)
        {
            return _schemaRepositoryLocator(schema);
        }

        private IEntriesToSchemaAdaptor GetSchemaAdaptor(SchemaType schema)
        {
            return _schemaAdaptorLocator(schema);
        }

        private IEnumerable<Entry> GetEntriesByNames(IEnumerable<string> list)
        {
            return _entryRepository.FindByNames(list);
        }

        private void ExportWhitelist(IEnumerable<Entry> entries,
            IEntriesToSchemaAdaptor schemaAdaptor,
            ISchemaRepository schemaRepository,
            string middlewareName = "",
            string path = "")
        {
            var schema = schemaAdaptor.GetSchema(entries, middlewareName);

            schemaRepository.Save(schema, path);
        }

        private string GetOptionalMiddlewareName(IpWhiteListSettings whiteListSettings, SchemaType schema)
        {
            switch (schema)
            {
                case SchemaType.TraefikIpWhitelistMiddlewareFile:
                    return whiteListSettings.TraefikMiddlewareSettings.Name;
                default:
                    return "";
            }
        }

        private string GetOptionalPath(IpWhiteListSettings whiteListSettings, SchemaType schema)
        {
            switch (schema)
            {
                case SchemaType.TraefikIpWhitelistMiddlewareFile:
                    return whiteListSettings.TraefikMiddlewareSettings.FilePath;
                default:
                    return "";
            }
        }
        private void ProcessWhitelistSettings()
        {
            var whitelistSettings = _optionsSnapshot.Value.IpWhiteListSettings;

            foreach (var whitelistSetting in whitelistSettings)
            {
                if (whitelistSetting.AllowedEntries.Count < 1) return;

                var schemaAdaptor = GetSchemaAdaptor(whitelistSetting.SchemaType);

                var schemaRepository = GetSchemaRepository(whitelistSetting.SchemaType);

                var entries = whitelistSetting.AllowedEntries.Count > 0
                    ? GetEntriesByNames(whitelistSetting.AllowedEntries)
                    : new List<Entry>();

                var middlewareName = GetOptionalMiddlewareName(whitelistSetting, whitelistSetting.SchemaType);

                var middlewarePath = GetOptionalPath(whitelistSetting, whitelistSetting.SchemaType);

                ExportWhitelist(entries, schemaAdaptor, schemaRepository, middlewareName, middlewarePath);
            }
        }

        private void UpdateEntries()
        {
            var entries = _optionsSnapshot.Value.Entries;

            foreach (var entry in entries)
            {
                try
                {
                    var resolvedIp = _fqdnResolver.GetIpAddressAsync(entry.Fqdn).Result;

                    var dbEntry = _entryRepository.GetByName(entry.Name);
                    dbEntry.CurrentIp = dbEntry.LatestIp;
                    dbEntry.LatestIp = resolvedIp.ToString();
                    dbEntry.Fqdn = entry.Fqdn;
                    dbEntry.Name = entry.Name;

                    _entryRepository.AddOrUpdate(dbEntry);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Error while processing entry -> {entry.Name} {entry.Fqdn}", e);
                }
            }
        }

        public void FqdnUpdateJob()
        {
            try
            {
                if (_working) return;

                lock (this)
                {
                    _working = true;
                }

                _logger.LogInformation(
                    $"Launching Job. Each : {_optionsSnapshot.Value.FqdnUpdateJobSeconds} second/s.");

                UpdateEntries();

                ProcessWhitelistSettings();

            }
            finally
            {
                _working = false;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting service...");

            _logger.LogInformation("Environment: " + _hostEnvironment.EnvironmentName);

            try
            {
                JobManager.Initialize();

                await Task.Run(() =>
                {
                    var fqdnUpdateJobSeconds = (_optionsSnapshot.Value.FqdnUpdateJobSeconds < 30)
                        ? 30 : _optionsSnapshot.Value.FqdnUpdateJobSeconds;

                    JobManager.AddJob(FqdnUpdateJob,
                        s =>
                        {
                            s.WithName("FqdnUpdate Job Process")
                                .ToRunEvery((int)fqdnUpdateJobSeconds)
                                .Seconds();
                            s.Execute();
                        });

                    //var exporterJobSeconds = (_optionsSnapshot.Value.ExporterJobSeconds < 30)
                    //    ? 30 : _optionsSnapshot.Value.FqdnUpdateJobSeconds;

                    //JobManager.AddJob(FqdnUpdateJob,
                    //    s =>
                    //    {
                    //        s.WithName("Exporter Job Process")
                    //            .ToRunEvery((int)exporterJobSeconds)
                    //            .Seconds();
                    //        s.Execute();
                    //    });

                }, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                throw;
            }

            _logger.LogInformation("Started");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            JobManager.RemoveAllJobs();

            _logger.LogInformation("Service and jobs are stopped");

            await Task.CompletedTask;
        }
    }
}
