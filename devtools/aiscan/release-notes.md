Assets for the AI-likelihood score in Pickles Playlist Editor. The app downloads these automatically, once,
when you turn the AI score on. Do not install them by hand.

## FFmpeg full build (`ffmpeg.exe`)
- Build: `9.0.2-full_build-www.gyan.dev`, from https://www.gyan.dev/ffmpeg/builds/ (archive: https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2)
- Licence: GPL v3. The full text is in the app's `licenses/FFmpeg-GPL-3.0.txt`.
- Source code: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b . The build configuration is printed by `ffmpeg.exe -buildconf`.
- Unmodified. Used for its libsoxr resampler.

## SONICS model (`sonics-beta-120s.onnx`, `sonics-beta-120s.onnx.data`)
- SpecTTTra-β (120 s) from SONICS: Synthetic Or Not - Identifying Counterfeit Songs, by Md Awsafur Rahman et al. https://github.com/awsaf49/sonics
- Weights: Hugging Face `awsaf49/sonics-spectttra-beta-120s`, revision `cc49422a98b5b92700876c608d0f9cdf27214a3e`, exported to ONNX with the spectrogram front end included.
- The model's training dataset is licensed CC BY-NC 4.0 (non-commercial); Pickles Playlist Editor is free and non-commercial.
- Licence of the code and weights (MIT, from https://github.com/awsaf49/sonics/blob/9156ffad151f797c71556923c4a02fa01fa8fc91/LICENSE):

```
MIT License

Copyright (c) 2023 awsaf49

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

CC BY-NC 4.0 License

The dataset is licensed under the Creative Commons Attribution-NonCommercial 4.0 International License. To view a copy of this license, visit http://creativecommons.org/licenses/by-nc/4.0/.

You are free to:
- Share: copy and redistribute the material in any medium or format
- Adapt: remix, transform, and build upon the material

Under the following terms:
- Attribution: You must give appropriate credit, provide a link to the license, and indicate if changes were made. You may do so in any reasonable manner, but not in any way that suggests the licensor endorses you or your use.
- NonCommercial: You may not use the material for commercial purposes.
- Additional Restrictions: The dataset may not be used for competing product research with respect to Suno and Udio platforms and services. The usage of this dataset should also comply with terms of services of the Suno and Udio platforms. 

You may not apply legal terms or technological measures that legally restrict others from doing anything the license permits.
```
