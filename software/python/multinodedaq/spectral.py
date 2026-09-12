"""Windowed FFT accumulation SCF, with bounded channel-pair batches."""
from dataclasses import dataclass
import numpy as np
from scipy import fft, signal
from .contracts import check

@dataclass(frozen=True)
class FAMSettings:
    rate: float = 25000
    df: float = 10
    dalpha: float = 5
    max_frequency: float = 5000
    max_alpha: float = 1000
    hop_fraction: float = .25
    pair_batch: int = 256

class FAM:
    def __init__(self, settings=FAMSettings()):
        self.settings=settings
        check(all(np.isfinite(v) and v>0 for v in (settings.rate,settings.df,settings.dalpha,settings.max_frequency,settings.max_alpha)), 'FAM frequencies')
        check(0<settings.hop_fraction<=.5 and 1<=settings.pair_batch<=4096,'FAM bounds')
        self.nfft=int(fft.next_fast_len(max(4,int(np.ceil(settings.rate/settings.df)))))
        self.hop=max(1,round(self.nfft*settings.hop_fraction))
        self.frames=int(fft.next_fast_len(max(4,int(np.ceil(settings.rate/(self.hop*settings.dalpha))))))
        self.count=(self.frames-1)*self.hop+self.nfft
        check(self.count<=1000000 and self.nfft<=16384 and self.frames<=4096,'FAM working bounds; decimate high-rate inputs first')
        self.df=settings.rate/self.nfft;self.dalpha=settings.rate/(self.hop*self.frames)
        check(self.frames*self.nfft*3*16<=128*1024**2,'channelizer memory bound')
        self.window=signal.windows.hamming(self.nfft,sym=False)
        self.bins=fft.fftshift(fft.fftfreq(self.nfft,1/settings.rate))
        residual=fft.fftshift(fft.fftfreq(self.frames,self.hop/settings.rate))
        # Retain the nearest coarse channel-difference cell; no aliased duplicate strips.
        self.q=np.flatnonzero((residual>=-self.df/2-1e-9)&(residual<self.df/2-1e-9))
        self.residual=residual[self.q]
        self.demod=np.exp(-2j*np.pi*np.arange(self.frames)[:,None]*self.hop*self.bins[None,:]/settings.rate)
        self.frequency=np.arange(int(np.floor(min(settings.max_frequency,settings.rate/2)/(self.df/2)))+1)*self.df/2
        self.alpha=np.arange(int(np.floor(min(settings.max_alpha,settings.rate)/self.dalpha))+1)*self.dalpha
        check(len(self.frequency)*len(self.alpha)<=1000000,'SCF output bound')
        self.pairs=[]
        # Plan once. Frequency coordinates are centered: f=(f_k+f_l)/2.
        for k,fk in enumerate(self.bins):
            lower=max(-fk,fk-settings.max_alpha-self.df/2)
            upper=min(2*settings.max_frequency-fk,fk+self.df/2)
            a,b=np.searchsorted(self.bins,[lower-1e-8,upper+1e-8])
            for l in range(a,b):
                if 0 <= (fk+self.bins[l])/2 <= settings.max_frequency+1e-8:self.pairs.append((k,l))
                check(len(self.pairs)<=250000,'pair limit; narrow bands or use coarser df')
        self.pairs=np.asarray(self.pairs,dtype=np.int32).reshape(-1,2)
        check(len(self.pairs)<=2000000,'FAM pair capacity')

    def compute(self, values, first_sample=0, scale=1.0):
        check(np.isrealobj(values),'real triaxial input required')
        x=np.asarray(values,dtype=np.float64)
        if x.ndim==1:x=x[:,None]
        check(x.ndim==2 and len(x)==self.count and 1<=x.shape[1]<=3 and np.isfinite(x).all(),'FAM input shape/finite')
        check(np.isfinite(scale) and scale>0,'calibration scale')
        x=x*scale
        windows=np.lib.stride_tricks.sliding_window_view(x,self.nfft,axis=0)[::self.hop]
        channel=fft.fftshift(fft.fft(windows*self.window,axis=-1,workers=1),axes=-1).transpose(0,2,1)
        channel*=self.demod[:,:,None]
        scf=np.zeros((len(self.frequency),len(self.alpha),x.shape[1]),dtype=np.complex128)
        hits=np.zeros(scf.shape[:2],dtype=np.uint32)
        normalization=self.frames*self.settings.rate*np.sum(self.window**2)
        for begin in range(0,len(self.pairs),self.settings.pair_batch):
            pairs=self.pairs[begin:begin+self.settings.pair_batch];k,l=pairs.T
            products=channel[:,k,:]*channel[:,l,:].conj()
            accum=fft.fftshift(fft.fft(products,axis=0,workers=1),axes=0)[self.q]/normalization
            centers=(self.bins[k]+self.bins[l])/2
            alpha=self.bins[k][None,:]-self.bins[l][None,:]+self.residual[:,None]
            fi=np.broadcast_to(np.rint(centers/(self.df/2)).astype(int),alpha.shape)
            ai=np.rint(alpha/self.dalpha).astype(int)
            # With noncommensurate hops, retain only coordinates on the reported grid.
            valid=(ai>=0)&(ai<len(self.alpha))&(fi>=0)&(fi<len(self.frequency))&(np.abs(alpha-ai*self.dalpha)<1e-7)
            accum*=np.exp(-2j*np.pi*alpha[:,:,None]*((first_sample % (self.hop*self.frames))/self.settings.rate))
            np.add.at(scf,(fi[valid],ai[valid]),accum[valid]);np.add.at(hits,(fi[valid],ai[valid]),1)
        np.divide(scf,hits[:,:,None],out=scf,where=hits[:,:,None]>0)
        return dict(frequency_hz=self.frequency,alpha_hz=self.alpha,scf=scf,valid_grid=hits>0,
                    algorithm='fam-hamming-v1',df_hz=self.df,dalpha_hz=self.dalpha,
                    first_sample=first_sample,count=self.count,hop=self.hop,frames=self.frames,
                    convention='centered f; manuscript asymmetric-lag coordinate is f-alpha/2',
                    density='two-sided',pair_count=len(self.pairs))


def psd(values, rate, scale=1.0):
    x=np.asarray(values,dtype=np.float64)*scale
    if x.ndim==1:x=x[:,None]
    check(len(x)>=4 and np.isfinite(x).all() and rate>0,'PSD input')
    frequency,power=signal.periodogram(x,fs=rate,window='hamming',detrend=False,scaling='density',axis=0)
    return dict(frequency_hz=frequency,psd=power,asd=np.sqrt(power),power=np.sum(power,axis=0)*rate/len(x),rms=np.sqrt(np.mean(x*x,axis=0)),mean=np.mean(x,axis=0))
