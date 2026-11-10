# python/cpython#137934: os.execve on Windows; ucrtbase construct_environment_block reads past the end of envp.
# The wait lets the collector's 10-second module snapshot see this process before it dies.
import os
import time

time.sleep(15)
prog = r"C:\Windows\System32\whoami.exe"
os.execve(prog, [prog], os.environ)
