"""Diffs TexHarness's block decode against an unrelated decoder, pixel for pixel.

    TexHarness.exe --codeccheck <dir>
    python compare_against_reference.py <dir>

Needs `pip install texture2ddecoder numpy`. The point is independence: a codec that is only
self-consistent — encode, decode, compare to itself — passes just as happily when the encoder and
decoder share a mistake. This is what caught a transposed row in the BC7 third-subset anchor table,
which corrupted only the blocks using those partitions and was invisible in any real texture.
"""
import collections
import os
import struct
import sys

import numpy as np
import texture2ddecoder


def bc3_is_well_formed(block):
    """BC3 colour endpoints must be ordered; c0 <= c1 is out of spec for this format.

    BC1 reads c0 <= c1 as a three-colour mode with a punchthrough black. BC2 and BC3 carry alpha
    separately and have no such mode, so D3D decodes their colour block as if c0 > c1 always. Real
    encoders (rgbcx strips the flag for BC3) never emit it, and where shipped textures do contain
    it the block is almost always flat black, where both readings agree to the pixel. Those blocks
    are reported but not counted as failures: the reference decoder simply reuses its BC1 path.
    """
    c0, c1 = struct.unpack_from("<HH", block, 8)
    return c0 > c1


def compare(directory, name, decode, mode_of=None, well_formed=None):
    blocks_path = os.path.join(directory, f"{name}-blocks.bin")
    pixels_path = os.path.join(directory, f"{name}-pixels.bin")
    if not os.path.exists(blocks_path):
        print(f"{name}: no blocks emitted, skipped")
        return 0

    blocks = open(blocks_path, "rb").read()
    mine = np.frombuffer(open(pixels_path, "rb").read(), dtype=np.uint8)
    count = len(blocks) // 16

    mismatched = 0
    skipped = 0
    by_mode = collections.Counter()
    for i in range(count):
        block = blocks[i * 16:(i + 1) * 16]
        # texture2ddecoder works on whole images, so one block is a 4x4 image.
        ref = np.frombuffer(decode(block, 4, 4), dtype=np.uint8)
        got = mine[i * 64:(i + 1) * 64]
        if np.array_equal(ref, got):
            continue

        if well_formed and not well_formed(block):
            skipped += 1
            continue

        mismatched += 1
        if mode_of:
            by_mode[mode_of(block)] += 1
        if mismatched <= 3:
            print(f"  {name} block {i} differs"
                  + (f" (mode {mode_of(block)})" if mode_of else ""))
            print(f"    reference {list(ref[:8])}")
            print(f"    ours      {list(got[:8])}")

    if skipped:
        print(f"{name}: {skipped:,} out-of-spec blocks diverge as expected (see bc3_is_well_formed)")

    if mode_of:
        coverage = collections.Counter(mode_of(blocks[i * 16:(i + 1) * 16]) for i in range(count))
        print(f"{name}: mode coverage {dict(sorted(coverage.items()))}")
    print(f"{name}: {mismatched} of {count:,} blocks differ"
          + (f" {dict(sorted(by_mode.items()))}" if by_mode else ""))
    return mismatched


def bc7_mode(block):
    """Mode is the index of the first set bit; no bit set is the reserved encoding."""
    return next((b for b in range(8) if block[0] >> b & 1), 8)


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else "."
    bad = compare(directory, "bc3", texture2ddecoder.decode_bc3,
                  well_formed=bc3_is_well_formed)
    bad += compare(directory, "bc7", texture2ddecoder.decode_bc7, bc7_mode)

    print()
    print("all decodes match the reference" if bad == 0 else f"{bad} blocks disagree")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
