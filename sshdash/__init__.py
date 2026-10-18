"""SSH Dashboard — a desktop host & key manager for OpenSSH.

The package is split into three layers:

``sshdash.model``
    Immutable value objects shared by every layer.
``sshdash.parsers``
    Read-only parsers for ``known_hosts``, ``ssh_config`` and key files.
``sshdash.store``
    The only module allowed to write: backups, atomic saves, activity log.

The user interface lives in :mod:`sshdash.ui` and never touches the
filesystem directly.
"""

__version__ = "1.0.0"
__app_name__ = "SSH Dashboard"
