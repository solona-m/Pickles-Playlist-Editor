"""Provenance of the aiscan-v1 model assets: exports SONICS SpecTTTra-beta 120 s, spectrogram included, to ONNX.

Input `audio` [1, 1920000] float32 at 16 kHz mono, output `logit` [1, 1]; sigmoid(logit) is the AI likelihood.
Source: Hugging Face awsaf49/sonics-spectttra-beta-120s @ cc49422a98b5b92700876c608d0f9cdf27214a3e, with the
`sonics` package from https://github.com/awsaf49/sonics. The torch export is not byte-reproducible, so a
re-export goes under a new aiscan tag with new pins; the published files are the pinned ones.
"""
import os
import sys

import torch
from sonics import HFAudioClassifier

REVISION = "cc49422a98b5b92700876c608d0f9cdf27214a3e"

out_dir = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
model = HFAudioClassifier.from_pretrained("awsaf49/sonics-spectttra-beta-120s", revision=REVISION)
model.eval()
win = int(model.config.audio.max_time * model.config.audio.sample_rate)
dummy = torch.randn(1, win) * 0.1

out = os.path.join(out_dir, "sonics-beta-120s.onnx")
with torch.no_grad():
    torch.onnx.export(model, (dummy,), out, input_names=["audio"], output_names=["logit"], dynamo=True, opset_version=18)
print("exported", out, os.path.getsize(out))
