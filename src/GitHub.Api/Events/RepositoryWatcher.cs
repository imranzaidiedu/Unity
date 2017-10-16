﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sfw.net;

namespace GitHub.Unity
{
    interface IRepositoryWatcher : IDisposable
    {
        void Start();
        void Stop();
        event Action HeadChanged;
        event Action IndexChanged;
        event Action ConfigChanged;
        event Action<string> LocalBranchChanged;
        event Action<string> LocalBranchCreated;
        event Action<string> LocalBranchDeleted;
        event Action RepositoryChanged;
        event Action<string, string> RemoteBranchCreated;
        event Action<string, string> RemoteBranchDeleted;
        void Initialize();
        int CheckAndProcessEvents();
    }

    class RepositoryWatcher : IRepositoryWatcher
    {
        private readonly RepositoryPathConfiguration paths;
        private readonly CancellationToken cancellationToken;
        private readonly NPath[] ignoredPaths;
        private readonly ManualResetEventSlim pauseEvent;
        private readonly bool disableNative;
        private NativeInterface nativeInterface;
        private bool running;
        private Task task;
        private int lastCountOfProcessedEvents = 0;
        private bool processingEvents;
        private readonly ManualResetEventSlim signalProcessingEventsDone = new ManualResetEventSlim(false);

        public event Action HeadChanged;
        public event Action IndexChanged;
        public event Action ConfigChanged;
        public event Action<string> LocalBranchChanged;
        public event Action<string> LocalBranchCreated;
        public event Action<string> LocalBranchDeleted;
        public event Action RepositoryChanged;
        public event Action<string, string> RemoteBranchCreated;
        public event Action<string, string> RemoteBranchDeleted;

        public RepositoryWatcher(IPlatform platform, RepositoryPathConfiguration paths, CancellationToken cancellationToken)
        {
            this.paths = paths;
            this.cancellationToken = cancellationToken;

            ignoredPaths = new[] {
                platform.Environment.UnityProjectPath.Combine("Library"),
                platform.Environment.UnityProjectPath.Combine("Temp")
            };

            pauseEvent = new ManualResetEventSlim();
            //disableNative = !platform.Environment.IsWindows;
        }

        public void Initialize()
        {
            var pathsRepositoryPath = paths.RepositoryPath.ToString();

            try
            {
                if (!disableNative)
                    nativeInterface = new NativeInterface(pathsRepositoryPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public void Start()
        {
            if (disableNative)
            {
                Logger.Trace("Native interface is disabled");
                return;
            }

            if (nativeInterface == null)
            {
                Logger.Warning("NativeInterface is null");
                throw new InvalidOperationException("NativeInterface is null");
            }

            Logger.Trace("Watching Path: \"{0}\"", paths.RepositoryPath.ToString());

            running = true;
            pauseEvent.Reset();
            task = Task.Factory.StartNew(WatcherLoop, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
        }

        public void Stop()
        {
            if (!running)
            {
                Logger.Warning("Watcher already stopped");
                return;
            }

            Logger.Trace("Stopping watcher");

            running = false;
            pauseEvent.Set();
        }

        private void WatcherLoop()
        {
            Logger.Trace("Starting watcher");

            while (running)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Stop();
                    break;
                }

                CheckAndProcessEvents();

                if (pauseEvent.Wait(1000))
                {
                    break;
                }
            }
        }

        public int CheckAndProcessEvents()
        {
            if (processingEvents)
            {
                signalProcessingEventsDone.Wait(cancellationToken);
                return lastCountOfProcessedEvents;
            }

            signalProcessingEventsDone.Reset();
            processingEvents = true;
            var processedEventCount = 0;

            var fileEvents = nativeInterface.GetEvents();
            if (fileEvents.Length > 0)
            {
                Logger.Trace("Handling {0} Events", fileEvents.Length);
                processedEventCount = ProcessEvents(fileEvents);
                Logger.Trace("Processed {0} Events", processedEventCount);
            }

            lastCountOfProcessedEvents = processedEventCount;
            processingEvents = false;
            signalProcessingEventsDone.Set();

            return processedEventCount;
        }

        enum EventType
        {
            None,
            ConfigChanged,
            HeadChanged,
            RepositoryChanged,
            IndexChanged,
            RemoteBranchDeleted,
            RemoteBranchCreated,
            RemoteBranchChanged,
            LocalBranchDeleted,
            LocalBranchCreated,
            LocalBranchChanged
        }

        class EventData
        {
            public string Origin;
            public string Branch;
        }

        private int ProcessEvents(Event[] fileEvents)
        {
            Dictionary<EventType, List<EventData>> events = new Dictionary<EventType, List<EventData>>();
            foreach (var fileEvent in fileEvents)
            {
                if (!running)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Stop();
                    break;
                }

                //Logger.Trace(fileEvent.Describe());

                var eventDirectory = new NPath(fileEvent.Directory);
                var fileA = eventDirectory.Combine(fileEvent.FileA);

                NPath fileB = null;
                if (fileEvent.FileB != null)
                {
                    fileB = eventDirectory.Combine(fileEvent.FileB);
                }

                // handling events in .git/*
                if (fileA.IsChildOf(paths.DotGitPath))
                {
                    if (!events.ContainsKey(EventType.ConfigChanged) && fileA.Equals(paths.DotGitConfig))
                    {
                        events.Add(EventType.ConfigChanged, null);
                    }
                    else if (!events.ContainsKey(EventType.HeadChanged) && fileA.Equals(paths.DotGitHead))
                    {
                        events.Add(EventType.HeadChanged, null);
                    }
                    else if (!events.ContainsKey(EventType.IndexChanged) && fileA.Equals(paths.DotGitIndex))
                    {
                        events.Add(EventType.IndexChanged, null);
                    }
                    else if (fileA.IsChildOf(paths.RemotesPath))
                    {
                        var relativePath = fileA.RelativeTo(paths.RemotesPath);
                        var relativePathElements = relativePath.Elements.ToArray();

                        if (!relativePathElements.Any())
                        {
                            continue;
                        }

                        var origin = relativePathElements[0];

                        if (fileEvent.Type == sfw.net.EventType.DELETED)
                        {
                            if (fileA.ExtensionWithDot == ".lock")
                            {
                                continue;
                            }

                            var branch = string.Join(@"/", relativePathElements.Skip(1).ToArray());
                            AddOrUpdateEventData(events, EventType.RemoteBranchDeleted, new EventData { Origin = origin, Branch = branch });
                        }
                        else if (fileEvent.Type == sfw.net.EventType.RENAMED)
                        {
                            if (fileA.ExtensionWithDot != ".lock")
                            {
                                continue;
                            }

                            if (fileB != null && fileB.FileExists())
                            {
                                if (fileA.FileNameWithoutExtension == fileB.FileNameWithoutExtension)
                                {
                                    var branchPathElement = relativePathElements
                                        .Skip(1).Take(relativePathElements.Length - 2)
                                        .Union(new[] { fileA.FileNameWithoutExtension }).ToArray();

                                    var branch = string.Join(@"/", branchPathElement);
                                    AddOrUpdateEventData(events, EventType.RemoteBranchCreated, new EventData { Origin = origin, Branch = branch });
                                }
                            }
                        }
                    }
                    else if (fileA.IsChildOf(paths.BranchesPath))
                    {
                        if (fileEvent.Type == sfw.net.EventType.MODIFIED)
                        {
                            if (fileA.DirectoryExists())
                            {
                                continue;
                            }

                            if (fileA.ExtensionWithDot == ".lock")
                            {
                                continue;
                            }

                            var relativePath = fileA.RelativeTo(paths.BranchesPath);
                            var relativePathElements = relativePath.Elements.ToArray();

                            if (!relativePathElements.Any())
                            {
                                continue;
                            }

                            var branch = string.Join(@"/", relativePathElements.ToArray());

                            AddOrUpdateEventData(events, EventType.LocalBranchChanged, new EventData { Branch = branch });

                        }
                        else if (fileEvent.Type == sfw.net.EventType.DELETED)
                        {
                            if (fileA.ExtensionWithDot == ".lock")
                            {
                                continue;
                            }

                            var relativePath = fileA.RelativeTo(paths.BranchesPath);
                            var relativePathElements = relativePath.Elements.ToArray();

                            if (!relativePathElements.Any())
                            {
                                continue;
                            }

                            var branch = string.Join(@"/", relativePathElements.ToArray());
                            AddOrUpdateEventData(events, EventType.LocalBranchDeleted, new EventData { Branch = branch });
                        }
                        else if (fileEvent.Type == sfw.net.EventType.RENAMED)
                        {
                            if (fileA.ExtensionWithDot != ".lock")
                            {
                                continue;
                            }

                            if (fileB != null && fileB.FileExists())
                            {
                                if (fileA.FileNameWithoutExtension == fileB.FileNameWithoutExtension)
                                {
                                    var relativePath = fileB.RelativeTo(paths.BranchesPath);
                                    var relativePathElements = relativePath.Elements.ToArray();

                                    if (!relativePathElements.Any())
                                    {
                                        continue;
                                    }

                                    var branch = string.Join(@"/", relativePathElements.ToArray());
                                    AddOrUpdateEventData(events, EventType.LocalBranchCreated, new EventData { Branch = branch });
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (events.ContainsKey(EventType.RepositoryChanged) || ignoredPaths.Any(ignoredPath => fileA.IsChildOf(ignoredPath)))
                    {
                        continue;
                    }
                    events.Add(EventType.RepositoryChanged, null);
                }
            }

            return FireEvents(events);
        }

        private void AddOrUpdateEventData(Dictionary<EventType, List<EventData>> events, EventType type, EventData data)
        {
            if (!events.ContainsKey(type))
                events.Add(type, new List<EventData>());
            events[type].Add(data);
        }

        private int FireEvents(Dictionary<EventType, List<EventData>> events)
        {
            int eventsProcessed = 0;
            if (events.ContainsKey(EventType.ConfigChanged))
            {
                Logger.Trace("ConfigChanged");
                ConfigChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.ContainsKey(EventType.HeadChanged))
            {
                Logger.Trace("HeadChanged");
                HeadChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.ContainsKey(EventType.IndexChanged))
            {
                Logger.Trace("IndexChanged");
                IndexChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.ContainsKey(EventType.RepositoryChanged))
            {
                Logger.Trace("RepositoryChanged");
                RepositoryChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.ContainsKey(EventType.LocalBranchCreated))
            {
                foreach (var evt in events[EventType.LocalBranchCreated])
                {
                    Logger.Trace($"LocalBranchCreated: {evt.Branch}");
                    LocalBranchCreated?.Invoke(evt.Branch);
                    eventsProcessed++;
                }
            }

            if (events.ContainsKey(EventType.LocalBranchChanged))
            {
                foreach (var evt in events[EventType.LocalBranchChanged])
                {
                    Logger.Trace($"LocalBranchChanged: {evt.Branch}");
                    LocalBranchChanged?.Invoke(evt.Branch);
                    eventsProcessed++;
                }
            }

            if (events.ContainsKey(EventType.LocalBranchDeleted))
            {
                foreach (var evt in events[EventType.LocalBranchDeleted])
                {
                    Logger.Trace($"LocalBranchDeleted: {evt.Branch}");
                    LocalBranchDeleted?.Invoke(evt.Branch);
                    eventsProcessed++;
                }
            }

            if (events.ContainsKey(EventType.RemoteBranchCreated))
            {
                foreach (var evt in events[EventType.RemoteBranchCreated])
                {
                    Logger.Trace($"RemoteBranchCreated: {evt.Origin}/{evt.Branch}");
                    RemoteBranchCreated?.Invoke(evt.Origin, evt.Branch);
                    eventsProcessed++;
                }
            }

            if (events.ContainsKey(EventType.RemoteBranchDeleted))
            {
                foreach (var evt in events[EventType.RemoteBranchDeleted])
                {
                    Logger.Trace($"RemoteBranchDeleted: {evt.Origin}/{evt.Branch}");
                    RemoteBranchDeleted?.Invoke(evt.Origin, evt.Branch);
                    eventsProcessed++;
                }
            }
            return eventsProcessed;
        }

        private bool disposed;
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    Stop();
                    if (nativeInterface != null)
                    {
                        nativeInterface.Dispose();
                        nativeInterface = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected static ILogging Logger { get; } = Logging.GetLogger<RepositoryWatcher>();
    }
}
