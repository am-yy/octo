# Lyrics source evaluation

This is the measurement behind the default lyrics source order, `LYRICS_SOURCES=kugou,lrclib,lyricsovh`.

## Method

- **Sample:** 300 songs picked at random from one real library (mostly hip-hop and rap, with some R&B, pop, rock and electronic), with the title, artist, album and length from their tags. Nothing was cleaned up first, so tagging mistakes such as an album name in the artist field or a remix tagged with the plain title are in the sample on purpose.
- **Code:** each source was asked through Octo's own source classes (`KugouLyricsSource`, `LrclibLyricsSource`, `NeteaseLyricsSource`), with the same title cleanup and the same identity rule Octo uses in production. The harness is `octo.Tests/LyricsSourceEvaluation.cs`. It runs only when `OCTO_LYRICS_EVAL` names a sample file.
- **Pace:** each service got at most one request a second. The three services ran side by side, and every request was a read.
- **Retries:** a lookup the service could not answer (a 503, a timeout) was tried twice more, after the wait the service asked for, and counted as an error either way.
- **Latency:** the time spent on the network for one lookup, all of that source's requests added up (KuGou needs two or three: a search, sometimes a catalogue search, and a download). The pacing is left out, because it is the harness being polite and not a cost Octo has.
- **Runs:** LRCLIB and NetEase were measured once, on 2026-09-27. KuGou was measured three times on the same day, the last one with the final matching code. The KuGou numbers in the table come from that last run; its latency is lower than a cold start, because the same searches had been made that morning, so the first run's figures are also given.

## Results

"Found" counts only lyrics that passed the identity check. The rule is the same title with the same kind of recording (so a remix or a live take never stands in for the original), the same artist, and a length within three seconds.

"Naive top hit is the wrong song" counts the source's own first search result, taken without that check, that is a different song. That is what an integration that trusts the search would have shown.

| Source | Found | Word-timed | Line-timed | Plain only | Instrumental | Naive top hit is the wrong song | Median latency | p95 latency | Errors on first try | Errors after 2 retries |
|---|---|---|---|---|---|---|---|---|---|---|
| KuGou | 259 (86%) | 259 (86%) | 0 | 0 | 0 | 7 of 263 (3%) | 844 ms (1373 ms cold) | 2571 ms (3359 ms cold) | 0 (0%) | 0 (0%) |
| LRCLIB | 272 (91%) | 0 | 246 (82%) | 22 (7%) | 4 | 101 of 278 (36%) | 1044 ms | 1643 ms | 73 (24%) | 7 (2%) |
| NetEase | 247 (82%) | 0 | 247 (82%) | 0 | 0 | 40 of 300 (13%) | 1186 ms | 2197 ms | 0 (0%) | 0 (0%) |

Taken together:

- **Coverage:** 283 of the 300 songs have lyrics from at least one source. KuGou and LRCLIB alone cover all 283, so NetEase adds nothing on this sample.
- **KuGou first, then LRCLIB:** 259 songs get word-timed lyrics, and 280 get timed lyrics of some kind.
- **Only LRCLIB:** 24 songs. KuGou missed them, mostly obscure uploads, a few where the library's tags name a different recording, and one where KuGou's catalogue credits the artist under another name.
- **Only KuGou:** 15 songs. LRCLIB had nothing, and it had only plain text for 19 more that KuGou has word-timed.
- **Uncertain matches:** 10 of KuGou's, 11 of LRCLIB's and 11 of NetEase's passed the check but either had no length to compare or were 2 to 3 seconds off. The library job puts these on its review list.

## Spot check

Fifteen found results were checked by hand: ten from KuGou, three from LRCLIB and two from NetEase, chosen at random. For each one the title and artist were right, the first lines were the song's real opening lines, and timing was present (word timing for every KuGou result, line timing for the others).

| Source | Song | First line |
|---|---|---|
| KuGou | Kid Cudi, Elsie's Baby Boy (flashback) | I'm never gonna get out of town am I Gordie |
| KuGou | Yelawolf and Eminem, Best Friend | Ain't never been much of the church type |
| KuGou | Drake, Can't Have Everything | Yeah uh man fresh up out the sand |
| KuGou | Russ, OLD DAYS | Riding 'round Atlanta windows down yeah I'm cruising |
| KuGou | Travis Scott, HOUSTONFORNICATION | Hmmm / I might need me some ventilation |
| KuGou | Jay Rock, Money Trees Deuce | I told my \*\*\*\*\*s if you hold me back |
| KuGou | Sueco, PRIMADONA | She say I'm her favorite it's probably 'cause I'm famous |
| KuGou | Mac Miller, So It Goes | Yeah / Yeah yeah |
| KuGou | Drake, Virginia Beach | I bet your mother would be proud of you ooh |
| KuGou | Joji, Gimme Love | Gimme gimme love |
| LRCLIB | The Weeknd, False Alarm | Bathroom stalls for the powder nose (she loves) |
| LRCLIB | Drake, Landed | Yeah, ayy, ayy |
| LRCLIB | Eminem, Killer (the remix, tagged "Killer") | Killer / Yeah, it's crazy, I'm a (killer) |
| NetEase | Post Malone, Spoil My Night | Yeah / I don't have much to say, I'll be out front |
| NetEase | Lil Peep, Rockstarz | The sun is out so put your screen down |

The check also turned up quirks in KuGou's text, all handled now and covered by tests:

- **Who sings next:** KuGou puts a speaker label on a line of its own ("Drake：") or ahead of the words ("Kanye West：Real friends"). The label is dropped and the words stay.
- **HTML entities:** some lyrics contain entities such as `&apos;`, which are decoded.
- **Credits:** a credit can be timed before its line (a negative word offset), and it is removed with the rest of the credits.
- **Name line:** a first line that only names the song ("Can't Tell Me Nothing - Ye", "Sunlight On Your Skin (Explicit) - Lil Peep/iLoveMakonnen") is dropped.

Some quirks remain and are left as they are:

- KuGou drops most punctuation and sometimes writes "i" for "I".
- It masks some words with asterisks.

## Decision

**KuGou first, then LRCLIB, then lyrics.ovh, with "prefer word-timed lyrics" on.**

- **Hit rate, read strictly:** LRCLIB finds lyrics of some kind for more songs, 91% against KuGou's 86%. The whole difference is plain text (22 songs) and instrumental markers (4). For timed lyrics, KuGou's hit rate is higher (86% against 82%), and every KuGou result is word-timed, which is what the Octo app's word-by-word view needs.
- **Errors:** KuGou's error rate was 0%. LRCLIB shed load on 24% of first tries (503 with a one-second Retry-After), and 2% still failed after two retries.
- **Wrong songs:** KuGou's own top search result was the wrong song 3% of the time, against 36% for LRCLIB's search. The identity check catches both, but it shows KuGou's search is the more precise.
- **Order:** with LRCLIB second, the order changes nothing about coverage, since the union is the same either way. It changes which answer wins when both have one, and how many services a song costs. KuGou first gives word timing wherever it exists at one service per song. LRCLIB first with "prefer word-timed" on would reach the same answers but ask both services for almost every song.
- **NetEase:** it stays opt-in. It found nothing on this sample that the other two did not, and its API is as unofficial as KuGou's.
- **Search cookie bug:** the evaluation found that NetEase answers any search that carries the cookie its first answer set with unrelated popular songs. So with the default HTTP handler, every NetEase search after the first came back wrong. Octo's lyrics clients no longer keep cookies.

## Risks

KuGou's API is unofficial: undocumented, unsigned, and free to change or disappear. Octo treats it that way:

- **Timeouts:** short (6 s per request).
- **Rate limits:** a 429 or 503 cools KuGou down for as long as it asks.
- **Circuit breaker:** after five failures in a row, KuGou is left alone for five minutes, and each lookup in that window is an instant "not now", not a wait.
- **Fallback:** a KuGou failure is never an error to the client, and the next source in the order answers.
- **Off switch:** leaving `kugou` out of `LYRICS_SOURCES` means it is never contacted.
