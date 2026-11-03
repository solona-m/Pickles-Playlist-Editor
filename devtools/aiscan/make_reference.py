"""Writes devtools/ScdAudioTests/Fixtures/aiscan/reference.json: the PyTorch SONICS logit for a synthetic signal
the C# tests rebuild exactly (AiScoreModelTests.Synthetic), so ONNX Runtime parity is checked without any music.

Run with the prototype venv (torch + the sonics package): python -I make_reference.py <out.json>
"""
import json
import math
import os
import sys

import numpy as np
import torch
from sonics import HFAudioClassifier

REVISION = "cc49422a98b5b92700876c608d0f9cdf27214a3e"
N = 1_920_000


def synthetic(variant):
    # Same formula as the C# test: computed in double, stored as float32.
    i = np.arange(N, dtype=np.float64)
    if variant == "tones":
        x = 0.3 * np.sin(2 * math.pi * 440.0 * i / 16000) + 0.1 * np.sin(2 * math.pi * 1234.5 * i / 16000)
    else:  # "chirp"
        t = i / 16000
        x = 0.25 * np.sin(2 * math.pi * (100.0 * t + 15.0 * t * t)) * (0.6 + 0.4 * np.sin(2 * math.pi * 0.5 * t))
    return x.astype(np.float32)


# from_pretrained has no revision argument, so load the pinned revision from a local snapshot folder:
# SONICS_MODEL_DIR = <hf cache>/models--awsaf49--sonics-spectttra-beta-120s/snapshots/<REVISION>
model_dir = os.environ["SONICS_MODEL_DIR"]
if os.path.basename(os.path.normpath(model_dir)) != REVISION:
    sys.exit(f"SONICS_MODEL_DIR must be the {REVISION} snapshot")
model = HFAudioClassifier.from_pretrained(model_dir)
model.eval()
out = {"revision": REVISION, "logits": {}}
with torch.no_grad():
    for v in ("tones", "chirp"):
        logit = float(model(torch.from_numpy(synthetic(v))[None, :]).reshape(-1)[0])
        out["logits"][v] = logit
        print(v, logit, 1 / (1 + math.exp(-logit)))
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(out, f, indent=2)
