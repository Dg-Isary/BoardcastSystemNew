import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/**
 * 经代理下载文件（本机 Schannel TLS 坏了，PowerShell/curl 走不了 HTTPS，但 Java 的 TLS 正常）。
 * 用法：java Downloader <url> <输出文件>
 */
public class Downloader {
    public static void main(String[] args) throws Exception {
        if (args.length < 2) { System.out.println("用法: java Downloader <url> <outFile>"); return; }
        String url = args[0];
        File out = new File(args[1]);
        File parent = out.getParentFile();
        if (parent != null) parent.mkdirs();
        File tmp = new File(out.getAbsolutePath() + ".part");
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setConnectTimeout(20000);
        c.setReadTimeout(120000);
        c.setInstanceFollowRedirects(true);
        c.setRequestProperty("User-Agent", "JavaDownloader/1.0");
        int code = c.getResponseCode();
        if (code != 200) { System.out.println("HTTP " + code + " for " + url); System.exit(2); }
        long total = c.getContentLengthLong();
        long got = 0, lastReport = 0;
        long t0 = System.currentTimeMillis();
        try (InputStream in = c.getInputStream(); FileOutputStream fos = new FileOutputStream(tmp)) {
            byte[] buf = new byte[256 * 1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                fos.write(buf, 0, n);
                got += n;
                long now = System.currentTimeMillis();
                if (now - lastReport > 3000) {
                    lastReport = now;
                    double sec = (now - t0) / 1000.0;
                    System.out.printf("  %d%%  %.1f MB / %.1f MB  (%.2f MB/s)%n",
                            total > 0 ? got * 100 / total : -1, got / 1048576.0, total / 1048576.0,
                            sec > 0 ? got / 1048576.0 / sec : 0);
                }
            }
            fos.flush();
        }
        if (out.exists()) out.delete();
        if (!tmp.renameTo(out)) { System.out.println("重命名失败: " + tmp); System.exit(3); }
        System.out.println("完成: " + out.getAbsolutePath() + "  " + got + " 字节");
    }
}
