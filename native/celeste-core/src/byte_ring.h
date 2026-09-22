// byte_ring.h — 单产单销字节环（Celeste 原生内核用）
// MIT。C# feeder 是唯一 push 方，原生渲染线程是唯一 pop 方。
// 用 mutex + 条件变量而非无锁队列：4096 帧/块、最长 15.6ms 一个周期，
// 锁竞争开销可忽略；换来实现简单、可证明正确（音频线程不抛异常、不分配）。
#pragma once

#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

class byte_ring {
public:
    void init(size_t capacity_bytes)
    {
        std::lock_guard<std::mutex> lk(m_);
        buf_.assign(capacity_bytes ? capacity_bytes : 1, 0);
        r_ = w_ = len_ = 0;
        stopped_ = false;
        input_ended_ = false;
    }

    // push 侧（feeder 线程）：阻塞直到全部写入或 stop 被请求。
    // 返回 false = 已停止（数据被丢弃，调用方应退出）。
    bool push(const void* data, size_t bytes)
    {
        std::unique_lock<std::mutex> lk(m_);
        const uint8_t* src = static_cast<const uint8_t*>(data);
        size_t left = bytes;
        while (left > 0) {
            if (stopped_) return false;
            const size_t space = buf_.size() - len_;
            if (space == 0) {
                // 满 = 背压：等渲染线程消费（stop 时立即放行）
                cv_space_.wait(lk, [this] { return stopped_ || buf_.size() - len_ > 0; });
                continue;
            }
            const size_t n = left < space ? left : space;
            const size_t first = buf_.size() - w_;  // 写到物理末尾的连续长度
            if (n <= first) {
                memcpy(buf_.data() + w_, src, n);
                w_ += n;
                if (w_ == buf_.size()) w_ = 0;
            } else {
                memcpy(buf_.data() + w_, src, first);
                memcpy(buf_.data(), src + first, n - first);
                w_ = n - first;
            }
            len_ += n;
            src += n;
            left -= n;
        }
        cv_data_.notify_one();
        return true;
    }

    // pop 侧（渲染线程）：最多取 bytes 字节；返回实取字节数（0 = 空）。
    size_t pop(void* out, size_t bytes)
    {
        std::lock_guard<std::mutex> lk(m_);
        const size_t n = len_ < bytes ? len_ : bytes;
        if (n == 0) return 0;
        uint8_t* dst = static_cast<uint8_t*>(out);
        const size_t first = buf_.size() - r_;
        if (n <= first) {
            memcpy(dst, buf_.data() + r_, n);
            r_ += n;
            if (r_ == buf_.size()) r_ = 0;
        } else {
            memcpy(dst, buf_.data() + r_, first);
            memcpy(dst + first, buf_.data(), n - first);
            r_ = n - first;
        }
        len_ -= n;
        cv_space_.notify_one();
        return n;
    }

    size_t ready() const
    {
        std::lock_guard<std::mutex> lk(m_);
        return len_;
    }

    // replace（seek/切歌）用：只允许 feeder 线程调用（单一 push 方）。
    void clear()
    {
        std::lock_guard<std::mutex> lk(m_);
        r_ = w_ = len_ = 0;
    }

    void request_stop()
    {
        {
            std::lock_guard<std::mutex> lk(m_);
            stopped_ = true;
        }
        cv_data_.notify_all();
        cv_space_.notify_all();
    }

    void mark_input_ended()
    {
        std::lock_guard<std::mutex> lk(m_);
        input_ended_ = true;
    }

    bool input_ended() const
    {
        std::lock_guard<std::mutex> lk(m_);
        return input_ended_;
    }

private:
    mutable std::mutex m_;
    std::condition_variable cv_data_;   // pop → push：有空位了
    std::condition_variable cv_space_;  // push → pop：有货了
    std::vector<uint8_t> buf_;
    size_t r_ = 0, w_ = 0, len_ = 0;
    bool stopped_ = false;
    bool input_ended_ = false;
};
