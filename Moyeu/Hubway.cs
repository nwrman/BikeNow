using System;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using Moyeu.Pronto;
using Android.App;
using Android.Widget;

namespace Moyeu
{
	public class Hubway : IObservable<Station[]>
	{
		//
		const string HubwayApiEndpoint = "http://bikenowapp.azurewebsites.net/cron/latest_stations.json";

		public static readonly Func<Station, bool> AvailableBikeStationPredicate = s => s.BikeCount > 1 && s.EmptySlotCount > 1;

		HttpClient client = new HttpClient ();
		TimeSpan freshnessTimeout;
		string savedData;

		List<HubwaySubscriber> subscribers = new List<HubwaySubscriber> ();

		public static readonly Hubway Instance = new Hubway ();

		public Hubway () : this (TimeSpan.FromMinutes (5))
		{

		}

		public Hubway (TimeSpan freshnessTimeout)
		{
			this.freshnessTimeout = freshnessTimeout;
			client.Timeout = TimeSpan.FromSeconds (30);
		}

		public DateTime LastUpdateTime {
			get;
			private set;
		}

		public Station[] LastStations {
			get;
			private set;
		}

		public static Station[] GetStationsAround (Station[] stations, GeoPoint location, double minDistance = 100, int maxItems = 4)
		{
			var dic = new SortedDictionary<double, Station> ();
			foreach (var s in stations) {
				var d = GeoUtils.Distance (location, s.Location);
				if (d < minDistance)
					dic.Add (d, s);
			}
			return dic.Select (ds => ds.Value).Take (maxItems).ToArray ();
		}

		public Station[] GetClosestStationTo (Station[] stations, params GeoPoint[] locations)
		{
			return GetClosestStationTo (stations, null, locations);
		}

		public Station[] GetClosestStationTo (Station[] stations, Func<Station, bool> filter, params GeoPoint[] locations)
		{
			var distanceToGeoPoints = new SortedDictionary<double, Station>[locations.Length];
			var ss = filter == null ? (IEnumerable<Station>)stations : stations.Where (filter);
			foreach (var station in ss) {
				for (int i = 0; i < locations.Length; i++) {
					if (distanceToGeoPoints [i] == null)
						distanceToGeoPoints [i] = new SortedDictionary<double, Station> ();
					distanceToGeoPoints [i].Add (GeoUtils.Distance (locations[i], station.Location), station);
				}
			}

			return distanceToGeoPoints.Select (ds => ds.First ().Value).ToArray ();
		}

		public bool HasCachedData {
			get {
				return savedData != null && DateTime.Now < (LastUpdateTime + freshnessTimeout);
			}
		}
		int attempt = 0;
		public async Task<Station[]> GetStations (bool forceRefresh = false, Action<string> dataCacher = null)
		{
			string data = null;

			if (forceRefresh)
				attempt = 0;

			if (HasCachedData && !forceRefresh)
				data = savedData;
			else {
				attempt++;
				while (data == null) {
					try {
						data = await client.GetStringAsync (HubwayApiEndpoint).ConfigureAwait (false);
						attempt = 0;
					} catch (Exception e) {
						Android.Util.Log.Error ("HubwayDownloader", e.ToString ());
						Xamarin.Insights.Report (e);
						if (attempt >= 3) {
							attempt = 0;
							return new Station[]{ };
						}
					}
					if (data == null)
						await Task.Delay (500);
				}
			}

			if (dataCacher != null)
				dataCacher (data);

			var stationRoot = Newtonsoft.Json.JsonConvert.DeserializeObject<ProntoRootObject> (data);
				
			var stations = stationRoot.Stations.Select (s => new Station {
				BikeCount = s.BikesAvailable,
				EmptySlotCount = s.DocksAvailable,
				Id = s.Id,
				Installed = !s.Planned,
				Location = new GeoPoint{Lat = s.Latitude, Lon = s.Longitude},
				Locked = s.OutOfService,
				Name = s.Name,
				Public = !s.Planned,
				Temporary = s.Planned,
				Street = s.Streets
			}).ToArray();

			LastUpdateTime = FromUnixTime (stationRoot.Timestamp);
			LastStations = stations;

			if (subscribers.Any ())
				foreach (var sub in subscribers)
					sub.Observer.OnNext (stations);
			return stations;
		}

		public Station[] ParseStationsFromXml (string data)
		{
			var doc = XDocument.Parse (data);

			savedData = data;
			LastUpdateTime = FromUnixTime (long.Parse (((string)doc.Root.Attribute ("lastUpdate"))));

			var stations = doc.Root.Elements ("station")
				.Where (station => !string.IsNullOrEmpty (station.Element ("latestUpdateTime").Value))
				.Select (station => new Station {
					Id = int.Parse (station.Element ("id").Value),
					Name = station.Element ("name").Value,
					Location = new GeoPoint {
						Lat = double.Parse (station.Element ("lat").Value),
						Lon = double.Parse (station.Element ("long").Value)
					},
					Installed = station.Element ("installed").Value == "true",
					Locked = station.Element ("locked").Value == "true",
					Temporary = station.Element ("temporary").Value == "true",
					Public = station.Element ("public").Value == "true",
					BikeCount = int.Parse (station.Element ("nbBikes").Value),
					EmptySlotCount = int.Parse (station.Element ("nbEmptyDocks").Value),
					//LastUpdateTime = FromUnixTime (long.Parse (station.Element ("latestUpdateTime").Value)),
				})
				.ToArray ();
			LastStations = stations;
			return stations;
		}

		DateTime FromUnixTime (long secs)
		{
			return (new DateTime (1970, 1, 1, 0, 0, 1, DateTimeKind.Utc) + TimeSpan.FromSeconds (secs / 1000.0)).ToLocalTime ();
		}

		public IDisposable Subscribe (IObserver<Station[]> observer)
		{
			var sub = new HubwaySubscriber (subscribers.Remove, observer);
			subscribers.Add (sub);
			return sub;
		}

		class HubwaySubscriber : IDisposable
		{
			Func<HubwaySubscriber, bool> unsubscribe;

			public HubwaySubscriber (Func<HubwaySubscriber, bool> unsubscribe, IObserver<Station[]> observer)
			{
				Observer = observer;
				this.unsubscribe = unsubscribe;
			}

			public IObserver<Station[]> Observer {
				get;
				set;
			}

			public void Dispose ()
			{
				unsubscribe (this);
			}
		}
	}
}

