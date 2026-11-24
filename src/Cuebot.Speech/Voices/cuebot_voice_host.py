"""Cuebot's voice engine host (MIT, part of Cuebot).

Runs in Cuebot's own Python environment for voices, in a process of its own, and speaks with sherpa-onnx
(Apache-2.0). Text becomes phonemes through piper_phonemize (GPL-3.0, espeak-ng), which is downloaded on demand into
this environment and never loaded into Cuebot itself.

Protocol: one JSON request per line on stdin, one JSON reply per line on stdout.
  {"id": 1, "op": "hello"}                       -> {"id": 1, "ok": true, "sherpa": "2.0.3", "phonemize": true}
  {"id": 2, "op": "speak", "kind": "vits"|"kokoro"|"kitten"|"matcha", "model": ..., "tokens": ..., "voices": ...,
   "vocoder": ... (matcha),
   "frontend": "espeak"|"text", "espeak_voice": "en-us", "clusters": [[["a", "ɪ"], ""], ...],
   "text": ..., "sid": 0, "speed": 1.0}          -> {"id": 2, "ok": true, "sample_rate": 22050, "wav": "<base64>"}
Errors: {"id": n, "ok": false, "error": "..."}.
"""
import array
import base64
import io
import json
import sys
import traceback
import unicodedata
import wave

import sherpa_onnx

_engines = {}


def _engine(req):
    key = (req["kind"], req["model"], req.get("tokens"), req.get("voices"), req.get("vocoder"))
    tts = _engines.get(key)
    if tts is None:
        if req["kind"] == "kokoro":
            model = sherpa_onnx.OfflineTtsModelConfig(
                kokoro=sherpa_onnx.OfflineTtsKokoroModelConfig(model=req["model"], voices=req["voices"], tokens=req["tokens"]),
                num_threads=2)
        elif req["kind"] == "kitten":
            model = sherpa_onnx.OfflineTtsModelConfig(
                kitten=sherpa_onnx.OfflineTtsKittenModelConfig(model=req["model"], voices=req["voices"], tokens=req["tokens"]),
                num_threads=2)
        elif req["kind"] == "matcha":
            model = sherpa_onnx.OfflineTtsModelConfig(
                matcha=sherpa_onnx.OfflineTtsMatchaModelConfig(acoustic_model=req["model"], vocoder=req["vocoder"], tokens=req["tokens"]),
                num_threads=2)
        else:
            model = sherpa_onnx.OfflineTtsModelConfig(
                vits=sherpa_onnx.OfflineTtsVitsModelConfig(model=req["model"], tokens=req["tokens"]), num_threads=2)
        tts = sherpa_onnx.OfflineTts(sherpa_onnx.OfflineTtsConfig(model=model))
        if len(_engines) >= 4:
            _engines.pop(next(iter(_engines)))
        _engines[key] = tts
    return tts


def _merge(phones, clusters):
    """Piper 1.5's _merge_known_vowel_clusters: adjacent phonemes forming a known cluster become one, longest first."""
    if not clusters:
        return phones
    known = {tuple(seq): symbol for seq, symbol in clusters}
    longest = max(len(k) for k in known)
    out, i = [], 0
    while i < len(phones):
        for size in range(longest, 1, -1):
            symbol = known.get(tuple(phones[i:i + size]))
            if symbol is not None:
                out.append(symbol)
                i += size
                break
        else:
            out.append(phones[i])
            i += 1
    return out


def _phonemes(req):
    text = req["text"]
    if req.get("frontend") == "text":
        return [[ord(c) for c in unicodedata.normalize("NFD", text)]]
    from piper_phonemize import phonemize_espeak

    clusters = req.get("clusters") or []
    sentences = []
    for sentence in phonemize_espeak(text, req.get("espeak_voice") or "en-us"):
        # Piper voices were trained on NFD phonemes (accents as separate symbols); Kokoro, Kitten and Matcha on
        # espeak-ng's own output, as sherpa-onnx's examples pass it.
        joined = "".join(sentence)
        phones = list(unicodedata.normalize("NFD", joined) if req["kind"] == "vits" else joined)
        sentences.append([ord(p) for p in _merge(phones, clusters)])
    return sentences


def _speak(req):
    tts = _engine(req)
    config = sherpa_onnx.GenerationConfig()
    config.sid = int(req.get("sid", 0))
    config.speed = float(req.get("speed", 1.0))
    config.phoneme_codepoints = _phonemes(req)
    audio = tts.generate("", config)
    if len(audio.samples) == 0:
        raise RuntimeError("The voice made no sound for this text.")
    pcm = array.array("h", (max(-32768, min(32767, int(s * 32767))) for s in audio.samples))
    if sys.byteorder == "big":
        pcm.byteswap()
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(audio.sample_rate)
        w.writeframes(pcm.tobytes())
    return {"sample_rate": audio.sample_rate, "wav": base64.b64encode(buffer.getvalue()).decode("ascii")}


def _hello():
    try:
        import piper_phonemize  # noqa: F401

        phonemize = True
    except Exception:
        phonemize = False
    return {"sherpa": getattr(sherpa_onnx, "__version__", "?"), "phonemize": phonemize}


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        reply = {"id": None, "ok": False}
        try:
            req = json.loads(line)
            reply["id"] = req.get("id")
            result = _hello() if req.get("op") == "hello" else _speak(req)
            reply.update(result)
            reply["ok"] = True
        except Exception as ex:
            reply["error"] = f"{type(ex).__name__}: {ex}"
            traceback.print_exc(file=sys.stderr)
        sys.stdout.write(json.dumps(reply) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
