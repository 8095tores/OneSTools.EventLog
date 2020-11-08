﻿using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OneSTools.EventLog.Exporter.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneSTools.EventLog.Exporter.SqlServer
{
    public class EventLogStorage : IEventLogStorage
    {
        private readonly IServiceProvider _serviceProvider;
        public EventLogStorage(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<(string FileName, long EndPosition)> ReadEventLogPositionAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            using var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var item = await context.EventLogItems.OrderByDescending(c => c.Id).FirstOrDefaultAsync();

            if (item == null)
                return ("", 0);
            else
                return (item.FileName, item.EndPosition);
        }

        public async Task WriteEventLogDataAsync<T>(List<T> entities, CancellationToken cancellationToken = default) where T : class, IEventLogItem
        {
            using var scope = _serviceProvider.CreateScope();
            using var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            context.ChangeTracker.AutoDetectChangesEnabled = false;
            await context.BulkInsertAsync(entities);
            context.ChangeTracker.AutoDetectChangesEnabled = true;

            await context.SaveChangesAsync();
        }

        public void Dispose()
        {
            
        }
    }
}
