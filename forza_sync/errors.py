"""自定义异常类型。

所有已知异常统一从 :class:`ForzaSyncError` 派生，便于上层统一捕获与处理。
"""


class ForzaSyncError(Exception):
    """所有已知错误的基类。"""


class ConfigError(ForzaSyncError):
    """配置读取 / 写入 / 校验错误。"""


class AuthError(ForzaSyncError):
    """Token 缺失、过期或无效（HTTP 401 / 403）。"""


class ApiError(ForzaSyncError):
    """API 请求失败（HTTP 非 2xx，且不属于鉴权 / 网络异常）。"""


class NetworkError(ForzaSyncError):
    """网络异常（连接失败、超时、DNS 解析失败等）。"""


class DownloadError(ForzaSyncError):
    """图片下载失败。"""


class DataFormatError(ForzaSyncError):
    """API 返回的 JSON 结构不符合预期（格式变化防护）。"""
