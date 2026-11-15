class PuddingPcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.samples = new Float32Array(2048);
    this.length = 0;
    this.done = false;
    this.port.onmessage = ({ data }) => {
      if (data === 'finish') {
        if (this.length) this.port.postMessage(this.samples.slice(0, this.length));
        this.done = true;
        this.port.postMessage('finished');
      }
    };
  }
  process(inputs) {
    if (this.done) return false;
    const channels = inputs[0];
    if (!channels?.length) return true;
    for (let i = 0; i < channels[0].length; i++) {
      let sample = 0;
      for (const channel of channels) sample += channel[i] / channels.length;
      this.samples[this.length++] = sample;
      if (this.length === this.samples.length) {
        this.port.postMessage(this.samples);
        this.samples = new Float32Array(2048);
        this.length = 0;
      }
    }
    return true;
  }
}
registerProcessor('pudding-pcm-capture', PuddingPcmCapture);
