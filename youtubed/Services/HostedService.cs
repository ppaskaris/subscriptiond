using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace youtubed.Services
{
    public abstract class HostedService : IHostedService
    {
        private Task _task;
        private CancellationTokenSource _cancellationTokenSource;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _task = ExecuteAsync(_cancellationTokenSource.Token);
            return _task.IsCompleted ? _task : Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_task != null)
            {
                _cancellationTokenSource.Cancel();
                await Task.WhenAny(_task, Task.Delay(-1, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        protected abstract Task ExecuteAsync(CancellationToken cancellationToken);
    }
}
