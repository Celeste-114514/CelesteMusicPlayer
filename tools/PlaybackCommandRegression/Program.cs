using System;
using CelesteMusicPlayer;
bool playing = false; int previous = 0, next = 0, toggles = 0;
var router = new PlaybackCommandRouter(() => playing, () => previous++, () => { toggles++; playing = !playing; }, () => next++);
void Check(bool value) { if (!value) throw new Exception("Command semantics regression"); }
Check(router.TryExecute("play") && playing && toggles == 1);
Check(router.TryExecute("play") && playing && toggles == 1);
Check(router.TryExecute("pause") && !playing && toggles == 2);
Check(router.TryExecute("pause") && !playing && toggles == 2);
Check(router.TryExecute("togglePlay") && playing && toggles == 3);
Check(router.TryExecute("next") && next == 1 && toggles == 3);
Check(router.TryExecute("previous") && previous == 1 && toggles == 3);
Check(!router.TryExecute(null) && !router.TryExecute("exit") && !router.TryExecute("NEXT"));
Check(next == 1 && previous == 1 && toggles == 3);
Console.WriteLine("PASS: shared command semantics, idempotent play/pause, exactly-once navigation and unknown commands");
