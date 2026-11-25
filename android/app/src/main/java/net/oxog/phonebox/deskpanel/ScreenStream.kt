package net.oxog.phonebox.deskpanel

import android.util.Base64
import java.io.DataInputStream
import java.io.EOFException

/**
 * Reads the agent's /api/screen (the Now playing widget's full-screen video) and hands every message to [deliver] as it
 * arrives. Not through shouldInterceptRequest like the panel's other requests: the WebView reads an intercepted
 * response in 64 KB blocks, so a stream of small video frames would reach the page in bursts every half second.
 *
 * [deliver] gets the message type (1 setup, 2 key frame, 3 frame, as ScreenFeed.cs sends them) and its payload in
 * base64; 0 with the agent's JSON answer when it refused; -1 when the stream ended.
 */
class ScreenStream(private val link: AgentLink, private val deliver: (type: Int, data: String) -> Unit) {

    private val gate = Any()
    @Volatile private var generation = 0
    private var current: AgentLink.Reply? = null

    /**
     * [width] x [height]: the largest picture the computer should send (the phone's screen, or the viewer's setting);
     * [quality]: 1 standard, 2 high, 3 max bitrate.
     */
    fun start(widget: String, width: Int, height: Int, quality: Int) {
        val gen = synchronized(gate) { stopLocked(); ++generation }
        Thread({ run(gen, widget, width, height, quality) }, "screen-stream").apply { isDaemon = true }.start()
    }

    fun stop() = synchronized(gate) { stopLocked() }

    private fun stopLocked() {
        ++generation
        val reply = current ?: return
        current = null
        // cut the connection on another thread: closing a stream that is still receiving can wait for it to drain,
        // and a new start() must not wait behind that (the computer would go on capturing for the old one)
        Thread({ cut(reply) }, "screen-stream-stop").apply { isDaemon = true }.start()
    }

    private fun cut(reply: AgentLink.Reply) {
        try { reply.connection?.disconnect() } catch (_: Exception) {}
        try { reply.body.close() } catch (_: Exception) {}
    }

    private fun alive(gen: Int) = gen == generation

    private fun run(gen: Int, widget: String, width: Int, height: Int, quality: Int) {
        val path = "/api/screen?widget=" + java.net.URLEncoder.encode(widget, "UTF-8") + "&w=$width&h=$height&q=$quality"
        val opened = link.stream(path)
        if (opened == null) { if (alive(gen)) deliver(0, """{"error":"offline"}"""); return }
        val reply = opened.second
        synchronized(gate) {
            if (!alive(gen)) { Thread { cut(reply) }.start(); return }
            current = reply
        }
        try {
            if (reply.code != 200) {
                val text = reply.body.bufferedReader().use { it.readText() }
                if (alive(gen)) deliver(0, text)
                return
            }
            val input = DataInputStream(reply.body.buffered(64 * 1024))
            while (alive(gen)) {
                val length = input.readInt()
                if (length < 1 || length > 8 * 1024 * 1024) break // not a message: the stream is broken
                val type = input.readUnsignedByte()
                val payload = ByteArray(length - 1)
                input.readFully(payload)
                if (!alive(gen)) break
                deliver(type, Base64.encodeToString(payload, Base64.NO_WRAP))
            }
        } catch (_: EOFException) {
        } catch (_: Exception) {
            // cut by stop(), or the connection dropped
        } finally {
            synchronized(gate) { if (current === reply) current = null }
            cut(reply)
            if (alive(gen)) deliver(-1, "")
        }
    }
}
