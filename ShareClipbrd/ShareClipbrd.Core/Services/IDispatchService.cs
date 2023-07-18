﻿using ShareClipbrd.Core.Clipboard;

namespace ShareClipbrd.Core.Services {
    public interface IDispatchService {
        void ReceiveData(ClipboardData clipboardData);
        void ReceiveFiles(IList<string> files);
        void ReceiveImage(ClipboardData clipboardData);
    }
}
