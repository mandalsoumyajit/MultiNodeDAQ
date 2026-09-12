"""MultiNodeDAQ contracts, recording access, and optional analysis."""
from .reader import open_session, read_samples

def subscribe(*args, **kwargs):
    from .client import subscribe as implementation
    return implementation(*args, **kwargs)

def export_hdf5(*args, **kwargs):
    from .export import export_hdf5 as implementation
    return implementation(*args, **kwargs)
