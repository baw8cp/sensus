﻿using Sensus.Exceptions;
using Sensus.UI.Properties;
using System;
using Xamarin.Geolocation;

namespace Sensus.Probes.Location
{
    public abstract class GpsProbe : ActivePassiveProbe
    {
        private EventHandler<PositionEventArgs> _positionChangedHandler;

        protected GpsProbe()
        {
            _positionChangedHandler = (o, e) =>
                {
                    if (Logger.Level >= LoggingLevel.Verbose)
                        Logger.Log("Received position change notification.");

                    StoreDatum(ConvertReadingToDatum(e.Position));
                };
        }

        /// <summary>
        /// Polls for a Datum from this GpsProbe. This is thread-safe, and concurrent calls will block to take new readings.
        /// </summary>
        /// <returns></returns>
        public override Datum Poll()
        {
            lock (this)
            {
                if (Logger.Level >= LoggingLevel.Verbose)
                    Logger.Log("Polling GPS receiver.");

                return ConvertReadingToDatum(GpsReceiver.Get().GetReading((Controller as ActiveProbeController).SleepDurationMS, 10000));
            }
        }

        public override void StartListening()
        {
            GpsReceiver.Get().AddListener(_positionChangedHandler);
        }

        public override void StopListening()
        {
            GpsReceiver.Get().RemoveListener(_positionChangedHandler);
        }

        protected abstract Datum ConvertReadingToDatum(Position reading);
    }
}