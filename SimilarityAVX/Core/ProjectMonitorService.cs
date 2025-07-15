using System;
using System.Threading;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSharpMcpServer.Core;

/// <summary>
/// Background service that runs ProjectMonitor for automatic project verification and monitoring.
/// </summary>
public class ProjectMonitorService : BackgroundService
{
    private readonly Configuration _configuration;
    private readonly ILogger<ProjectMonitorService> _logger;
    private ProjectMonitor? _monitor;

    public ProjectMonitorService(Configuration configuration, ILogger<ProjectMonitorService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if monitoring is enabled
        if (!_configuration.Monitoring.EnableAutoReindex)
        {
            _logger.LogInformation("[ProjectMonitorService] Automatic reindexing is disabled in configuration.");
            return;
        }
        
        _logger.LogInformation("[ProjectMonitorService] Starting automatic project monitoring...");
        
        try
        {
            _monitor = new ProjectMonitor(_configuration);
            
            // Verify all projects on startup if enabled
            if (_configuration.Monitoring.VerifyOnStartup)
            {
                await _monitor.VerifyAllProjectsAsync();
            }
            else
            {
                _logger.LogInformation("[ProjectMonitorService] Startup verification is disabled in configuration.");
            }
            
            // Keep the service running until cancellation is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            _logger.LogInformation("[ProjectMonitorService] Service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProjectMonitorService] An error occurred while running the monitor service");
            throw;
        }
    }

    public override void Dispose()
    {
        _monitor?.Dispose();
        base.Dispose();
    }
}