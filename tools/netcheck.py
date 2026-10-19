#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
订阅网络诊断工具（独立脚本，不属于广播系统程序本体）

用途：在一台"浏览器能访问、但程序同步失败"的电脑上跑一次，输出一份报告，
      用来判断到底是 ①DNS 劫持 ②TCP 被丢包 ③TLS 被复位(中间设备) ④服务端 WAF/限流
      ⑤本机 TLS 栈/证书 ⑥本机代理设置 哪一种。

特性：
  - 只用 Python 标准库，无需 pip 安装任何东西
  - 全程只读：GET 一个公开的节假日 JSON，不上传、不修改任何系统设置
  - 带对照组：百度（国内，应当成功），用来区分"整机 TLS 坏了"和"只是这几个域名被拦"
  - 直连 IP 测试：绕开 DNS，用来区分"DNS 问题"和"路由/策略问题"
  - 重复测试：默认 5 次、间隔 20 秒，用来识别"限流 / 延迟白名单"这类时好时坏的规则

用法（在那台有问题的电脑上）：
    python netcheck.py
 或
    py -3 netcheck.py
报告位置：脚本同目录下 netcheck-report-YYYYmmdd-HHMMSS.txt（同时打印到屏幕）
"""

import os
import ssl
import sys
import json
import time
import socket
import platform
import datetime
import subprocess
import urllib.request
import urllib.error

# ------------------------- 被测目标 -------------------------
TARGETS = [
    ("cdn.jsdelivr.net", "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/2026.json"),
    ("raw.githubusercontent.com", "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/2026.json"),
    ("raw.fastgit.org", "https://raw.fastgit.org/NateScarlet/holiday-cn/master/2026.json"),
    ("raw.gitmirror.com", "https://raw.gitmirror.com/NateScarlet/holiday-cn/master/2026.json"),
]
CONTROL_HOSTS = [("www.baidu.com", "https://www.baidu.com/")]

DOH_ENDPOINTS = [
    ("AliDNS(223.5.5.5)", "https://223.5.5.5/resolve?name={name}&type=A"),
    ("Cloudflare(1.1.1.1)", "https://1.1.1.1/dns-query?name={name}&type=A"),
]

REPEAT_TIMES = 5          # 重复测试次数（识别限流/延迟放行）
REPEAT_GAP_SEC = 20       # 每次间隔秒数
if len(sys.argv) > 1 and sys.argv[1].lower() in ("quick", "fast", "-q"):
    REPEAT_TIMES, REPEAT_GAP_SEC = 2, 5      # 快速模式：先看整体情况
CONNECT_TIMEOUT = 10      # TCP 连接超时
READ_TIMEOUT = 25         # 读写超时

BROWSER_HEADERS = {
    "accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
    "accept-language": "zh-CN,zh;q=0.9,en;q=0.8",
    "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "(KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36 Edg/153.0.0.0",
}

report_lines = []


def out(text=""):
    print(text)
    report_lines.append(text)


def section(title):
    out()
    out("=" * 72)
    out(title)
    out("=" * 72)


def safe(fn, default=None):
    try:
        return fn()
    except Exception as ex:
        return "{}: {}".format(type(ex).__name__, ex) if default is None else default


# ------------------------- 1. 环境信息 -------------------------
def env_info():
    section("1. 环境信息")
    out("时间            : " + datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S %z"))
    out("操作系统        : " + platform.platform())
    out("Python          : " + sys.version.replace("\n", " "))
    out("主机名          : " + safe(socket.gethostname, "?"))
    out("是否管理员      : " + str(safe(lambda: subprocess.run(
        ["net", "session"], capture_output=True).returncode == 0, "?")))

    # 系统时间偏差提醒（时间差太多会导致所有 HTTPS 证书校验失败）
    out("提示            : 若系统时间与实际相差很大，所有 HTTPS 都会失败，请先校准时间。")

    # 代理设置（注册表 Internet 选项）——程序已不使用代理，这里仅用于排除干扰
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                             r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
        enable = safe(lambda: winreg.QueryValueEx(key, "ProxyEnable")[0], 0)
        server = safe(lambda: winreg.QueryValueEx(key, "ProxyServer")[0], "")
        pac = safe(lambda: winreg.QueryValueEx(key, "AutoConfigURL")[0], "")
        out("系统代理开关    : {}  代理服务器: {}  PAC: {}".format(enable, server or "-", pac or "-"))
    except Exception as ex:
        out("系统代理        : 读取失败（{}）".format(ex))

    # hosts 文件里是否有这几个域名的固定映射
    try:
        hosts = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                             "System32", "drivers", "etc", "hosts")
        hits = []
        with open(hosts, "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                for host, _ in TARGETS:
                    if host in line:
                        hits.append(line)
        out("hosts 相关条目  : " + ("；".join(hits) if hits else "无（正常）"))
    except Exception as ex:
        out("hosts 检查      : 失败（{}）".format(ex))


# ------------------------- 2. DNS -------------------------
def dns_info():
    section("2. DNS 解析：系统解析 vs 加密DNS(DoH)")
    out("如果系统解析出的 IP 与 DoH 不一致，说明 DNS 被劫持/污染（浏览器可能自带 DoH 所以没事）。")
    for host, _ in TARGETS + CONTROL_HOSTS:
        sys_ips = safe(lambda h=host: sorted({ai[4][0] for ai in socket.getaddrinfo(h, 443,
                      proto=socket.IPPROTO_TCP)}), "解析失败")
        doh_ips = []
        for label, tpl in DOH_ENDPOINTS:
            url = tpl.format(name=host)
            req = urllib.request.Request(url, headers={"accept": "application/dns-json"})
            try:
                with urllib.request.urlopen(req, timeout=8) as resp:
                    data = json.loads(resp.read().decode("utf-8", "ignore"))
                ips = sorted({a.get("data") for a in data.get("Answer", []) if a.get("type") == 1})
                if ips:
                    doh_ips.append("{}={}".format(label.split("(")[0], ",".join(ips)))
            except Exception as ex:
                doh_ips.append("{}失败({})".format(label.split("(")[0], type(ex).__name__))
        out()
        out("[{}]".format(host))
        out("  系统解析 : {}".format(sys_ips))
        out("  加密解析 : {}".format("；".join(doh_ips) if doh_ips else "-"))


# ------------------------- 3. TCP 连接 -------------------------
def tcp_test(host, port=443):
    started = time.time()
    try:
        infos = socket.getaddrinfo(host, port, proto=socket.IPPROTO_TCP)
    except Exception as ex:
        return False, 0, "DNS失败: {}".format(ex), None
    ip = infos[0][4][0]
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(CONNECT_TIMEOUT)
    try:
        s.connect((ip, port))
        cost = (time.time() - started) * 1000
        return True, cost, "OK", ip
    except Exception as ex:
        cost = (time.time() - started) * 1000
        return False, cost, "{}: {}".format(type(ex).__name__, ex), ip
    finally:
        try:
            s.close()
        except Exception:
            pass


# ------------------------- 4. TLS 握手 -------------------------
def tls_test(host, ip=None, only_tls12=False):
    """返回 (是否成功, 说明, 详情dict)。关键：区分"服务端 alert"与"中间设备 RST"。"""
    ctx = ssl.create_default_context()
    if only_tls12:
        ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        ctx.maximum_version = ssl.TLSVersion.TLSv1_2
    raw = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    raw.settimeout(CONNECT_TIMEOUT)
    try:
        target_ip = ip or socket.getaddrinfo(host, 443, proto=socket.IPPROTO_TCP)[0][4][0]
        raw.connect((target_ip, 443))
    except Exception as ex:
        raw.close()
        return False, "TCP连接失败: {}: {}".format(type(ex).__name__, ex), {}
    raw.settimeout(READ_TIMEOUT)
    detail = {}
    try:
        tls = ctx.wrap_socket(raw, server_hostname=host)
        detail["协议"] = tls.version()
        detail["密码套件"] = tls.cipher()[0] if tls.cipher() else "-"
        cert = tls.getpeercert()
        if cert:
            detail["证书主体"] = str(cert.get("subject"))
            detail["颁发者"] = str(cert.get("issuer"))
            detail["有效期"] = "{} ~ {}".format(cert.get("notBefore"), cert.get("notAfter"))
        tls.close()
        return True, "握手成功", detail
    except ssl.SSLCertVerificationError as ex:
        raw.close()
        return False, "证书校验失败（服务端回复正常，是本机不信任该证书链）: {}".format(ex), detail
    except ssl.SSLError as ex:
        raw.close()
        # 有 alert 文本 => 对端/中间设备明确拒绝；仅 EOF/reset => 多半是中间设备掐断
        txt = str(ex)
        if "alert" in txt.lower():
            return False, "TLS 被明确拒绝（收到 alert，可能是服务端或中间设备的策略）: {}".format(txt), detail
        return False, "TLS 握手被中断（未收到 alert，多为中间设备复位）: {}".format(txt), detail
    except (ConnectionResetError, ConnectionAbortedError, OSError) as ex:
        raw.close()
        return False, "TLS 握手期间连接被复位（典型中间设备行为）: {}: {}".format(type(ex).__name__, ex), detail
    except Exception as ex:
        raw.close()
        return False, "握手异常: {}: {}".format(type(ex).__name__, ex), detail


# ------------------------- 5. HTTP 取数 -------------------------
def http_test(url, use_headers=True):
    import http.client
    from urllib.parse import urlsplit
    parts = urlsplit(url)
    ctx = ssl.create_default_context()
    try:
        conn = http.client.HTTPSConnection(parts.netloc, 443, timeout=READ_TIMEOUT, context=ctx)
        headers = dict(BROWSER_HEADERS) if use_headers else {}
        path = parts.path + (("?" + parts.query) if parts.query else "")
        conn.request("GET", path, headers=headers)
        resp = conn.getresponse()
        body = resp.read()
        conn.close()
        info = "HTTP {}  长度 {} 字节  Server={}".format(resp.status, len(body),
                                                        resp.getheader("server") or "-")
        ok_json = False
        try:
            data = json.loads(body.decode("utf-8", "ignore"))
            ok_json = True
            if isinstance(data, dict) and "days" in data:
                days = data["days"]
                info += "  JSON有效: {} 条 days".format(len(days))
            elif isinstance(data, list):
                info += "  JSON有效: {} 条记录".format(len(data))
        except Exception:
            info += "  内容不是JSON"
        return True, info, ok_json
    except Exception as ex:
        return False, "{}: {}".format(type(ex).__name__, ex), False


# ------------------------- 6. 直连 IP -------------------------
def ip_direct_test(host):
    """绕开 DNS，直接用解析到的 IP 连接（仍带原 SNI/Host），用于区分 DNS 与路由策略。"""
    try:
        ip = socket.getaddrinfo(host, 443, proto=socket.IPPROTO_TCP)[0][4][0]
    except Exception as ex:
        return "DNS 解析失败，无法做直连测试: {}".format(ex)
    ok, msg, _ = tls_test(host, ip=ip)
    return "直连 {}:443（SNI={}）→ {}{}".format(ip, host, "成功" if ok else "失败",
                                              "" if ok else "；" + msg)


# ------------------------- 主流程 -------------------------
def main():
    section("订阅网络诊断报告")
    out("生成时间: " + datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
    env_info()
    dns_info()

    section("3. TCP 443 连接测试")
    for host, _ in TARGETS + CONTROL_HOSTS:
        ok, cost, msg, ip = tcp_test(host)
        out("[{}] IP={}  {:.0f} ms  {}".format(host, ip or "-", cost, "成功" if ok else "失败 " + msg))

    section("4. TLS 握手测试（系统默认 TLS 版本）")
    for host, _ in TARGETS + CONTROL_HOSTS:
        ok, msg, detail = tls_test(host)
        out("[{}] {}".format(host, "成功" if ok else "失败"))
        if ok:
            for k, v in detail.items():
                out("      {}: {}".format(k, v))
        else:
            out("      " + msg)

    section("5. TLS 1.2 强制测试（排除版本协商问题）")
    for host, _ in TARGETS + CONTROL_HOSTS:
        ok, msg, _ = tls_test(host, only_tls12=True)
        out("[{}] {}".format(host, "成功" if ok else "失败：" + msg))

    section("6. 直连 IP 测试（绕开 DNS）")
    for host, _ in TARGETS:
        out("[{}] {}".format(host, ip_direct_test(host)))

    section("7. HTTP 取数测试")
    for host, url in TARGETS + CONTROL_HOSTS:
        ok, info, ok_json = http_test(url, use_headers=True)
        out("[{}] 带浏览器头 → {}".format(host, info if ok else "失败 " + info))
        if host == "cdn.jsdelivr.net":
            ok2, info2, _ = http_test(url, use_headers=False)
            out("[{}] 无自定义头 → {}".format(host, info2 if ok2 else "失败 " + info2))

    section("8. 重复测试（识别限流 / 延迟放行：{} 次，间隔 {} 秒）".format(REPEAT_TIMES, REPEAT_GAP_SEC))
    for host, url in TARGETS[:1] + CONTROL_HOSTS:
        out("[{}]".format(host))
        for i in range(1, REPEAT_TIMES + 1):
            ok, info, _ = http_test(url, use_headers=True)
            out("    第 {} 次: {}".format(i, info if ok else "失败 " + info))
            if i < REPEAT_TIMES:
                time.sleep(REPEAT_GAP_SEC)

    section("9. 结论提示")
    out("请把整份报告发回，重点看：")
    out("  · 第 2 节：系统解析 IP 与加密解析是否一致（不一致 → DNS 被劫持）")
    out("  · 第 4 节：失败时是'收到 alert'还是'未收到 alert 被中断'")
    out("       收到 alert     → 服务端/中间设备有明确策略（例如按客户端指纹或限流）")
    out("       未收到 alert   → 中间设备直接复位连接（与程序无关）")
    out("  · 第 6 节：直连 IP 若成功 → 属于 DNS 层问题；若同样失败 → 属于路由/策略层问题")
    out("  · 第 8 节：时好时坏 → 限流或高峰拥塞；一直失败 → 固定策略")
    out("  · 对照组 www.baidu.com 若也失败 → 本机 TLS 栈/证书有问题，与目标站点无关")

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "netcheck-report-{}.txt".format(stamp))
    try:
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(report_lines))
        print()
        print("报告已保存: " + path)
    except Exception as ex:
        print("报告保存失败: {}".format(ex))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n已中断")
    if sys.stdin and sys.stdin.isatty():
        input("\n按回车键退出…")
