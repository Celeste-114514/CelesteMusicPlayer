using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CelesteMusicPlayer;

for (int from = 0; from < 5; from++)
for (int to = 0; to < 5; to++)
for (int current = -1; current < 5; current++)
{
    var queue = new ObservableCollection<Track>(Enumerable.Range(0, 5).Select(i => new Track(i, i < 2 ? "same-file.cue" : $"{i}.flac")));
    var playing = current < 0 ? null : queue[current];
    var moved = queue[from];
    int moves = 0;
    queue.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Move) moves++; };
    int next = WebQueueRules.Move(queue, from, to, current);
    if (!ReferenceEquals(queue[to], moved)) throw new Exception("Moved occurrence is incorrect");
    if (playing == null ? next != -1 : !ReferenceEquals(queue[next], playing)) throw new Exception("Playback moved to another song or CUE segment");
    if (moves != (from == to ? 0 : 1)) throw new Exception("Move must preserve collection and emit a single move");
}
var duplicate = new Track(0, "repeat.flac");
var repeated = new ObservableCollection<Track>(new[] { duplicate, new Track(1, "other.flac"), duplicate });
if (WebQueueRules.Move(repeated, 2, 0, 2) != 0) throw new Exception("Exact duplicate occurrence lost");
Console.WriteLine("PASS: 150 reorder/current-index cases, CUE identity, repeated occurrence, single collection move, idle queue");
sealed record Track(int Segment, string Path);
