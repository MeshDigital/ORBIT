# 🧪 Acapella Factory: High-Performance Stem Separation

The Acapella Factory is ORBIT's dedicated multithreaded engine for batch audio isolation. It allows DJs to strip vocals or instrumentals from entire folders of music with DAW-grade precision and optimized resource management.

## 🧠 The 30-Second Chunking Strategy

To prevent the application from exhausting system RAM when processing long tracks (e.g., 10-minute extended mixes), the engine utilizes a **Sliding Window Chunking** pattern:

1. **NAudio Buffer Streaming**: The source file is streamed in 30-second floating-point chunks.
2. **ONNX Isolation**: Each chunk is passed to the AI model (Spleeter/UVB based) in a standalone task.
3. **Memory Reset**: RAM buffers are cleared between every chunk, maintaining a flat memory plateau (approx. 300MB overhead) regardless of track length.
4. **Re-weaving**: Isolated stems are re-joined into a high-quality WAV/FLAC output.

## ⚡ Hardware & Model Isolation

The Acapella Factory runs on a dedicated background thread pool (`AcapellaFactoryService`).
- **Concurrency**: Limited to `Environment.ProcessorCount / 2` to ensure the system remains responsive for music playback during separation.
- **Model Storage**: Pre-trained ONNX models are stored in `Tools/StemModels/` and lazily loaded on the first separation request.

## 📡 Pulse Telemetry UI

When a batch separation is active, ORBIT provides non-intrusive global feedback:
- **Header Pulse**: A glowing Electric Violet animation appears in the top command bar, indicating background AI activity.
- **Progress Overlay**: A secondary percentage indicator appears over the current setlist, reflecting the overall batch completion status.
- **Auto-Deactivate**: Once a target is queued, the Bulk Action sidebar automatically deactivates, allowing the DJ to return to library management while the AI grinds in the background.

## 🚀 Usage

1. Select multiple tracks in the **Collection Grid**.
2. Open the **Bulk Action Sidebar**.
3. Select **Isolation Mode** (Vocals Only / Instrumental Only).
4. Hit **START FACTORY**.
5. Separated files are automatically routed to your `Isolated/` subfolder and re-imported into the ORBIT library.
