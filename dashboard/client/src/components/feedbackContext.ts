import { createContext, useContext } from 'react'
import type { FeedbackSource } from '../hooks/useApi'

export interface FeedbackContextValue {
  openFeedback: (source?: FeedbackSource) => void
}

export const FeedbackContext = createContext<FeedbackContextValue>({ openFeedback: () => {} })

export function useFeedback() {
  return useContext(FeedbackContext)
}
