# Third-party notices

## WFHelper Riven OCR assets and pipeline

RelicFrame includes the following files from the WFHelper project:

- `RelicFrame.Bot/riven-ocr/yolo/stat_line_detector.onnx`
- `RelicFrame.Bot/riven-ocr/paddle/ch_PP-OCRv3_rec_infer.onnx`
- `RelicFrame.Bot/riven-ocr/paddle/ch_dict.txt`

The pinned SHA-256 values checked before model loading are:

- `D3C29D871AE1872D507E284607E657E7DDC05BC56343CD3BA9AF46F3E483160A`
- `897A3EDEDB38FEE0DAE2C1CCEE38241F37DF202C9509E3ABCA02E9217C5EE615`
- `C084FA990ECF0CE7FCB6BD5A3B689382645EC454E6F6AF712B3D6BE0ADDFDAF5`

The C# detector, recognizer, bounded tensor handling, OCR cleanup, and split-line
reconciliation in `RivenOnnxOcr.cs` are adapted from WFHelper's
`services/rivenOcrOnnx.ts`. They are used locally and do not send images or text
to a backend.

MIT License

Copyright (c) 2026 WFHelper

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
